"""System monitor thread: tracks active window and logs activity."""
import threading
import time
from datetime import datetime

import psutil
from sqlalchemy import desc

try:
    import win32gui
    import win32process
except ImportError:
    win32gui = None
    win32process = None

from database import SessionLocal
from models import ActivityLog


# Default user_id for activity logging (can be set by API when user is selected).
CURRENT_USER_ID = 1


def get_foreground_process_name() -> tuple[str | None, str | None]:
    """Return (process_name, window_title) for the foreground window, or (None, None)."""
    if not win32gui or not win32process:
        return None, None
    try:
        hwnd = win32gui.GetForegroundWindow()
        if not hwnd:
            return None, None
        _, pid = win32process.GetWindowThreadProcessId(hwnd)
        window_title = win32gui.GetWindowText(hwnd) or ""
        try:
            proc = psutil.Process(pid)
            name = proc.name() if proc else None
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            name = None
        return name, window_title
    except Exception:
        return None, None


class SystemMonitor(threading.Thread):
    """Daemon thread that watches the active window and logs activity to the database."""

    def __init__(self, interval_seconds: float = 5.0, user_id: int | None = None):
        super().__init__(daemon=True)
        self.interval_seconds = interval_seconds
        self._user_id = user_id if user_id is not None else CURRENT_USER_ID
        self._last_app_name: str | None = None
        self._last_window_title: str | None = None
        self._stop = threading.Event()

    def set_user_id(self, user_id: int) -> None:
        self._user_id = user_id

    def stop(self) -> None:
        self._stop.set()

    def run(self) -> None:
        while not self._stop.is_set():
            app_name, window_title = get_foreground_process_name()
            if app_name is None:
                app_name = "unknown"
            if window_title is None:
                window_title = ""

            db = SessionLocal()
            try:
                if app_name != self._last_app_name or window_title != self._last_window_title:
                    # App or window changed: create a new ActivityLog entry
                    log = ActivityLog(
                        user_id=self._user_id,
                        app_name=app_name,
                        window_title=window_title,
                        timestamp=datetime.utcnow(),
                        duration_seconds=int(self.interval_seconds),
                        category=None,
                    )
                    db.add(log)
                    db.commit()
                    self._last_app_name = app_name
                    self._last_window_title = window_title
                else:
                    # Same app: update duration_seconds of the latest entry for this user
                    latest = (
                        db.query(ActivityLog)
                        .filter(
                            ActivityLog.user_id == self._user_id,
                            ActivityLog.app_name == app_name,
                        )
                        .order_by(desc(ActivityLog.timestamp))
                        .first()
                    )
                    if latest:
                        latest.duration_seconds = (latest.duration_seconds or 0) + int(
                            self.interval_seconds
                        )
                        db.commit()

                # FUTURE: Insert process killing logic here
            finally:
                db.close()

            self._stop.wait(timeout=self.interval_seconds)
