"""AI judge: determines if a video is allowed given the user's goal (Groq + cache)."""
import json
import os
from typing import Any

from dotenv import load_dotenv

load_dotenv()

# In-memory cache: (video_title, user_goal) -> allowed (bool)
_judge_cache: dict[tuple[str, str], bool] = {}

GROQ_API_KEY = os.getenv("GROQ_API_KEY")
GROQ_MODEL = "llama3-8b-8192"


def _call_groq(video_title: str, user_goal: str) -> bool:
    """Call Groq API and parse JSON response. Returns allowed (bool)."""
    if not GROQ_API_KEY:
        # No API key: default to allowed to avoid blocking
        return True
    try:
        from groq import Groq
        client = Groq(api_key=GROQ_API_KEY)
        prompt = (
            f"User Goal: {user_goal}. Video: {video_title}. "
            "strict JSON response: {\"allowed\": bool}"
        )
        response = client.chat.completions.create(
            model=GROQ_MODEL,
            messages=[{"role": "user", "content": prompt}],
            temperature=0,
        )
        text = response.choices[0].message.content.strip()
        # Extract JSON (handle markdown code blocks)
        if "```" in text:
            start = text.find("{")
            end = text.rfind("}") + 1
            if start >= 0 and end > start:
                text = text[start:end]
        data: dict[str, Any] = json.loads(text)
        return bool(data.get("allowed", True))
    except Exception:
        return True


def judge_video(video_title: str, user_goal: str) -> bool:
    """
    Judge whether a video is allowed given the user's goal.
    Uses a local cache first; on cache miss, calls Groq API and caches the result.
    """
    cache_key = (video_title.strip(), user_goal.strip())
    if cache_key in _judge_cache:
        return _judge_cache[cache_key]
    result = _call_groq(video_title, user_goal)
    _judge_cache[cache_key] = result
    return result
