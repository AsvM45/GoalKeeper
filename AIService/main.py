"""
GoalKeeper AI Microservice
FastAPI service wrapping the Groq LLM for intelligent website/app classification.
Runs on localhost:8099. Starts automatically with the installer.
"""

from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
import uvicorn

from ai_engine import AIEngine

app = FastAPI(
    title="GoalKeeper AI Service",
    description="Groq-powered website and app classifier for GoalKeeper",
    version="2.0.0"
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)

engine = AIEngine()


@app.on_event("startup")
async def startup():
    await engine.initialize()


@app.post("/classify")
async def classify(request: dict):
    """
    Classify a website or app against the user's stated goals.

    Body:
      url          : str  - domain or app name
      window_title : str  - current window title
      app_name     : str  - executable name
      user_goals   : list - user's stated productivity goals
    """
    url = request.get("url", "")
    window_title = request.get("window_title", "")
    app_name = request.get("app_name", "")
    user_goals = request.get("user_goals", [])

    if not url and not app_name:
        raise HTTPException(status_code=400, detail="url or app_name required")

    result = await engine.classify(
        url=url,
        window_title=window_title,
        app_name=app_name,
        user_goals=user_goals
    )
    return result


@app.post("/judge")
async def judge_video(request: dict):
    """
    Legacy endpoint: judge whether a video title aligns with user goals.
    """
    video_title = request.get("video_title", "")
    user_goal = request.get("goal", "")

    if not video_title:
        raise HTTPException(status_code=400, detail="video_title required")

    result = await engine.judge_video(video_title, user_goal)
    return result


@app.get("/health")
async def health():
    return {"status": "ok", "groq_available": engine.groq_available}


if __name__ == "__main__":
    uvicorn.run("main:app", host="127.0.0.1", port=8099, reload=False)
