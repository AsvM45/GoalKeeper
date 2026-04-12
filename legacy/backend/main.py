"""GoalKeeper FastAPI application: API endpoints and startup."""
from contextlib import asynccontextmanager
from datetime import datetime, date, timedelta
from typing import Any

from fastapi import FastAPI, Depends, HTTPException
from pydantic import BaseModel
from sqlalchemy import func
from sqlalchemy.orm import Session

from database import engine, SessionLocal, Base
from models import User, ActivityLog, Goal
from ai_engine import judge_video
from monitor import SystemMonitor

# Global monitor instance; started on startup.
_system_monitor: SystemMonitor | None = None


def get_db():
    db = SessionLocal()
    try:
        yield db
    finally:
        db.close()


def get_system_monitor() -> SystemMonitor | None:
    return _system_monitor


# --- Pydantic schemas ---


class CheckVideoRequest(BaseModel):
    video_title: str
    user_id: int


class CheckVideoResponse(BaseModel):
    allowed: bool


class SetGoalRequest(BaseModel):
    user_id: int
    description: str
    is_strict: bool = False


class TopApp(BaseModel):
    name: str
    minutes: int


class StatsResponse(BaseModel):
    top_apps: list[TopApp]
    productivity_score: int


# --- Lifespan: create tables and start monitor ---


@asynccontextmanager
async def lifespan(app: FastAPI):
    yield
    global _system_monitor
    if _system_monitor:
        _system_monitor.stop()


app = FastAPI(title="GoalKeeper", lifespan=lifespan)


@app.on_event("startup")
async def startup_event():
    """Ensure SystemMonitor starts automatically on startup."""
    global _system_monitor
    Base.metadata.create_all(bind=engine)
    _system_monitor = SystemMonitor(interval_seconds=5.0)
    _system_monitor.start()


# --- Endpoints ---


@app.post("/check_video", response_model=CheckVideoResponse)
def check_video(
    body: CheckVideoRequest,
    db: Session = Depends(get_db),
) -> CheckVideoResponse:
    """Check if a video is allowed for the user based on their goal."""
    goal = db.query(Goal).filter(Goal.user_id == body.user_id).first()
    if not goal:
        raise HTTPException(status_code=404, detail="User has no goal set")
    allowed = judge_video(body.video_title, goal.description)
    return CheckVideoResponse(allowed=allowed)


@app.get("/stats", response_model=StatsResponse)
def stats(db: Session = Depends(get_db)) -> StatsResponse:
    """Aggregate ActivityLog for the current day; return top apps and productivity score."""
    today_start = datetime.combine(date.today(), datetime.min.time())
    today_end = today_start + timedelta(days=1)

    # Top apps by total duration (minutes) today
    rows = (
        db.query(ActivityLog.app_name, func.sum(ActivityLog.duration_seconds).label("total_sec"))
        .filter(
            ActivityLog.timestamp >= today_start,
            ActivityLog.timestamp < today_end,
        )
        .group_by(ActivityLog.app_name)
        .order_by(func.sum(ActivityLog.duration_seconds).desc())
        .limit(10)
        .all()
    )
    top_apps = [
        TopApp(name=name, minutes=int(total_sec / 60) if total_sec else 0)
        for name, total_sec in rows
    ]

    # Productivity score: simple heuristic 0–100 (e.g. work category vs entertainment)
    work_keywords = {"code", "vs", "cursor", "terminal", "slack", "teams", "outlook"}
    total_sec = sum((r[1] or 0) for r in rows)
    work_sec = 0
    for name, sec in rows:
        name_lower = (name or "").lower()
        if any(k in name_lower for k in work_keywords):
            work_sec += sec or 0
    productivity_score = 85
    if total_sec > 0:
        productivity_score = min(100, max(0, int(100 * work_sec / total_sec)))
    elif not rows:
        productivity_score = 85

    return StatsResponse(top_apps=top_apps, productivity_score=productivity_score)


@app.post("/set_goal")
def set_goal(
    body: SetGoalRequest,
    db: Session = Depends(get_db),
) -> dict[str, Any]:
    """Create or update the user's goal."""
    goal = db.query(Goal).filter(Goal.user_id == body.user_id).first()
    if goal:
        goal.description = body.description
        goal.is_strict = body.is_strict
    else:
        goal = Goal(
            user_id=body.user_id,
            description=body.description,
            is_strict=body.is_strict,
        )
        db.add(goal)
    db.commit()
    db.refresh(goal)
    return {"ok": True, "goal_id": goal.id}
