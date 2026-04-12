"""
GoalKeeper AI Engine - Enhanced for website/app classification.
Uses Groq (llama3-70b) for intelligent, goal-aware content judgment.
Falls back to a static heuristic classifier when Groq is unavailable.
"""

import os
import re
import json
import sqlite3
import asyncio
from datetime import datetime, timedelta
from pathlib import Path
from dotenv import load_dotenv

load_dotenv()

try:
    from groq import AsyncGroq
    GROQ_AVAILABLE = True
except ImportError:
    GROQ_AVAILABLE = False

# ── Static heuristic rules (fallback when Groq is offline) ───────────────────

PRODUCTIVE_PATTERNS = [
    r"github\.com", r"stackoverflow\.com", r"docs\.", r"developer\.",
    r"learn\.", r"tutorial", r"documentation", r"code", r"vs\s?code",
    r"visual\s?studio", r"cursor", r"terminal", r"console", r"ide",
    r"leetcode", r"hackerrank", r"kaggle", r"arxiv", r"scholar",
    r"notion", r"obsidian", r"confluence", r"jira", r"linear",
    r"slack", r"teams", r"outlook", r"gmail\.com/mail",
]

DISTRACTING_PATTERNS = [
    r"youtube\.com", r"netflix\.com", r"twitch\.tv", r"reddit\.com",
    r"twitter\.com", r"x\.com", r"instagram\.com", r"tiktok\.com",
    r"facebook\.com", r"discord\.com", r"9gag\.com", r"buzzfeed\.com",
    r"hulu\.com", r"disneyplus\.com", r"espn\.com", r"sports",
    r"gaming", r"steam", r"epicgames", r"roblox",
]


class AIEngine:
    def __init__(self):
        self.api_key = os.getenv("GROQ_API_KEY", "")
        self.client = AsyncGroq(api_key=self.api_key) if GROQ_AVAILABLE and self.api_key else None
        self.groq_available = self.client is not None

        # In-memory cache (supplements the SQLite AICache table)
        self._cache: dict[str, dict] = {}
        self._db_path = self._find_db()

    def _find_db(self) -> str | None:
        """Locate the GoalKeeper SQLite DB."""
        common = Path(os.environ.get("PROGRAMDATA", "C:/ProgramData")) / "GoalKeeper" / "metrics.sqlite"
        if common.exists():
            return str(common)
        return None

    async def initialize(self):
        """Load cached AI decisions from SQLite into memory."""
        if not self._db_path:
            return
        try:
            conn = sqlite3.connect(self._db_path)
            cursor = conn.execute(
                "SELECT UrlOrApp, Judgment, Confidence, Reason, Category FROM AICache "
                "WHERE CachedAt > datetime('now', '-24 hours') OR UserOverride IS NOT NULL"
            )
            for row in cursor.fetchall():
                self._cache[row[0]] = {
                    "judgment": row[4] if row[4] else row[1],  # UserOverride takes priority
                    "confidence": row[2],
                    "reason": row[3],
                    "category": row[4],
                }
            conn.close()
        except Exception as e:
            print(f"[AIEngine] Could not load DB cache: {e}")

    async def classify(
        self,
        url: str,
        window_title: str,
        app_name: str,
        user_goals: list[str],
    ) -> dict:
        """
        Classify a website or app. Returns:
          judgment    : 'allow' | 'block' | 'distraction'
          confidence  : 0.0 – 1.0
          reason      : human-readable explanation
          category    : 'productive' | 'distracting' | 'neutral'
        """
        cache_key = url or app_name

        # Check in-memory cache first
        if cache_key in self._cache:
            return self._cache[cache_key]

        # Try Groq first
        if self.groq_available and user_goals:
            try:
                result = await self._groq_classify(url, window_title, app_name, user_goals)
                self._cache[cache_key] = result
                await self._save_to_db(cache_key, result)
                return result
            except Exception as e:
                print(f"[AIEngine] Groq classify error: {e}")

        # Fall back to static heuristics
        result = self._heuristic_classify(url, window_title, app_name)
        self._cache[cache_key] = result
        return result

    async def _groq_classify(
        self,
        url: str,
        window_title: str,
        app_name: str,
        user_goals: list[str],
    ) -> dict:
        goals_str = "\n".join(f"- {g}" for g in user_goals)
        prompt = f"""You are a productivity assistant evaluating whether a website or app is appropriate for a user with the following goals:

{goals_str}

Evaluate this activity:
- URL/Domain: {url or '(app only)'}
- Window title: {window_title or '(none)'}
- Application: {app_name}

Respond ONLY with valid JSON in this exact format:
{{
  "judgment": "allow" | "block" | "distraction",
  "confidence": <float 0.0-1.0>,
  "reason": "<brief explanation max 20 words>",
  "category": "productive" | "distracting" | "neutral"
}}

Rules:
- "allow" = clearly aligned with user goals (productive work)
- "block" = clearly harmful/distracting with no productive angle
- "distraction" = potentially distracting, let the user decide with friction
- When in doubt, use "distraction" (never block without high confidence)
- Do not block unknown apps – only clearly recognized distractions"""

        response = await self.client.chat.completions.create(
            model="llama3-70b-8192",
            messages=[{"role": "user", "content": prompt}],
            temperature=0.1,
            max_tokens=150,
        )

        text = response.choices[0].message.content.strip()

        # Extract JSON even if surrounded by markdown code blocks
        json_match = re.search(r'\{.*?\}', text, re.DOTALL)
        if not json_match:
            raise ValueError(f"Could not parse JSON from Groq response: {text}")

        data = json.loads(json_match.group())
        return {
            "judgment": data.get("judgment", "distraction"),
            "confidence": float(data.get("confidence", 0.5)),
            "reason": data.get("reason", "AI classification"),
            "category": data.get("category", "neutral"),
        }

    def _heuristic_classify(self, url: str, window_title: str, app_name: str) -> dict:
        """Rule-based fallback classifier."""
        combined = f"{url} {window_title} {app_name}".lower()

        for pattern in PRODUCTIVE_PATTERNS:
            if re.search(pattern, combined, re.IGNORECASE):
                return {
                    "judgment": "allow",
                    "confidence": 0.7,
                    "reason": "Matches productive pattern (offline heuristic)",
                    "category": "productive",
                }

        for pattern in DISTRACTING_PATTERNS:
            if re.search(pattern, combined, re.IGNORECASE):
                return {
                    "judgment": "distraction",
                    "confidence": 0.7,
                    "reason": "Matches distracting pattern (offline heuristic)",
                    "category": "distracting",
                }

        return {
            "judgment": "allow",
            "confidence": 0.3,
            "reason": "No matching rule – allowing by default",
            "category": "neutral",
        }

    async def judge_video(self, video_title: str, user_goal: str) -> dict:
        """Legacy video title judgment."""
        cache_key = f"video:{video_title}:{user_goal}"
        if cache_key in self._cache:
            return self._cache[cache_key]

        if self.groq_available:
            try:
                prompt = f"""User goal: {user_goal}
Video title: {video_title}

Is watching this video aligned with the user's goal?
Respond only with JSON: {{"allowed": true|false, "reason": "<10 words max>"}}"""

                response = await self.client.chat.completions.create(
                    model="llama3-8b-8192",
                    messages=[{"role": "user", "content": prompt}],
                    temperature=0.1,
                    max_tokens=60,
                )
                text = response.choices[0].message.content.strip()
                json_match = re.search(r'\{.*?\}', text, re.DOTALL)
                if json_match:
                    data = json.loads(json_match.group())
                    result = {"allowed": bool(data.get("allowed", True)), "reason": data.get("reason", "")}
                    self._cache[cache_key] = result
                    return result
            except Exception as e:
                print(f"[AIEngine] judge_video error: {e}")

        return {"allowed": True, "reason": "AI unavailable – allowing by default"}

    async def _save_to_db(self, key: str, result: dict):
        """Persist classification result to SQLite AICache table."""
        if not self._db_path:
            return
        try:
            conn = sqlite3.connect(self._db_path)
            conn.execute(
                """INSERT INTO AICache (UrlOrApp, Judgment, Confidence, Reason, Category)
                   VALUES (?, ?, ?, ?, ?)
                   ON CONFLICT(UrlOrApp) DO UPDATE SET
                       Judgment = excluded.Judgment,
                       Confidence = excluded.Confidence,
                       Reason = excluded.Reason,
                       Category = excluded.Category,
                       CachedAt = datetime('now'),
                       UserOverride = NULL""",
                (key, result["judgment"], result["confidence"], result["reason"], result["category"])
            )
            conn.commit()
            conn.close()
        except Exception as e:
            print(f"[AIEngine] DB save error: {e}")
