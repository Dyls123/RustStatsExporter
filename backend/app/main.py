# backend/app/main.py
from typing import List, Optional, Dict, Any
from fastapi import FastAPI, APIRouter, Depends, Header, HTTPException
from pydantic import BaseModel, Field
from sqlalchemy.orm import Session

from .database import get_session
from . import crud
from .config import settings

app = FastAPI(title="Rust Stats API")
router = APIRouter()

from fastapi.middleware.cors import CORSMiddleware

# allow your frontend origins
app.add_middleware(
    CORSMiddleware,
    allow_origins=["http://localhost:8080", "http://127.0.0.1:8080"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


# ----- Schemas -----
class PlayerIn(BaseModel):
    user_id: int
    last_name: Optional[str] = None
    k: Dict[str, float] = Field(default_factory=dict)
    highest_range_kill_m: Optional[float] = 0.0

class IngestPayload(BaseModel):
    server_unix_time: int
    players: List[PlayerIn] = Field(default_factory=list)


# ----- Health -----
@router.get("/health")
def health() -> Dict[str, Any]:
    return {"ok": True}


# ----- Read APIs -----
@router.get("/keys")
def list_keys(db: Session = Depends(get_session)) -> Dict[str, List[str]]:
    return {"keys": crud.list_keys(db)}

@router.get("/leaderboard/{key}")
def leaderboard(key: str, limit: int = 50, db: Session = Depends(get_session)) -> List[Dict[str, Any]]:
    rows = crud.top_leaderboard(db, key, limit)
    return [{"user_id": uid, "last_name": name, "value": value} for uid, name, value in rows]

@router.get("/players/search")
def players_search(q: str = "", limit: int = 20, db: Session = Depends(get_session)) -> List[Dict[str, Any]]:
    return crud.search_players(db, q, limit)


# ----- Ingest (fixed: flush player BEFORE counters) -----
@router.post("/ingest")
def ingest(
    payload: IngestPayload,
    db: Session = Depends(get_session),
    x_api_key: Optional[str] = Header(default=None)
) -> Dict[str, Any]:
    # Optional API key gate
    if settings.API_KEY and x_api_key != settings.API_KEY:
        raise HTTPException(status_code=401, detail="invalid key")

    processed = 0
    try:
        for pl in payload.players or []:
            uid = int(pl.user_id)
            name = pl.last_name or str(uid)

            # Upsert player and FLUSH so FK inserts work
            crud.upsert_player(db, uid, name, payload.server_unix_time)
            db.flush()

            # Add additive counters
            if pl.k:
                crud.add_counters(db, uid, dict(pl.k))

            # Max counter for highest range kill
            if pl.highest_range_kill_m and pl.highest_range_kill_m > 0:
                crud.set_max_counter(db, uid, "highest_range_kill.m", float(pl.highest_range_kill_m))

            processed += 1

        db.commit()
        return {"ok": True, "players": processed}
    except Exception as e:
        db.rollback()
        # log to stdout so `docker compose logs api` shows the reason
        import traceback
        print("INGEST ERROR:", e)
        traceback.print_exc()
        raise HTTPException(status_code=500, detail="ingest failed")


# Mount router
app.include_router(router)

# Uvicorn entrypoint (optional, used only if you run `python -m app.main`)
if __name__ == "__main__":
    import uvicorn
    uvicorn.run("app.main:app", host="0.0.0.0", port=8000, reload=False)

from fastapi import FastAPI, APIRouter, Depends, Header, HTTPException, Query
from typing import List, Optional, Dict, Any
from sqlalchemy.orm import Session

from .database import get_session, Base, engine
from . import crud, models
from .config import settings

app = FastAPI(title="Rust Stats API")
router = APIRouter()

@app.on_event("startup")
def init_db():
    Base.metadata.create_all(bind=engine)

# ----- existing schemas in this file are fine -----

@router.get("/health")
def health(db: Session = Depends(get_session)) -> Dict[str, Any]:
    return {"ok": True, "wipe_started": crud.get_wipe_started(db)}

@router.get("/keys")
def list_keys(scope: str = Query("wipe", pattern="^(wipe|lifetime)$"),
              db: Session = Depends(get_session)) -> Dict[str, List[str]]:
    return {"keys": crud.list_keys(db, scope=scope)}

@router.get("/leaderboard/{key}")
def leaderboard(key: str,
                limit: int = 50,
                scope: str = Query("wipe", pattern="^(wipe|lifetime)$"),
                db: Session = Depends(get_session)) -> List[Dict[str, Any]]:
    rows = crud.top_leaderboard(db, key, limit, scope=scope)
    return [{"user_id": uid, "last_name": name, "value": value} for uid, name, value in rows]

@router.post("/ingest")
def ingest(payload: IngestPayload,
           db: Session = Depends(get_session),
           x_api_key: Optional[str] = Header(default=None)) -> Dict[str, Any]:
    if settings.API_KEY and x_api_key != settings.API_KEY:
        raise HTTPException(status_code=401, detail="invalid key")

    processed = 0
    try:
        for pl in payload.players or []:
            uid = int(pl.user_id)
            name = pl.last_name or str(uid)

            crud.upsert_player(db, uid, name, payload.server_unix_time)
            db.flush()

            if pl.k:
                # lifetime
                crud.add_counters(db, uid, dict(pl.k))
                # current wipe
                crud.add_counters_wipe(db, uid, dict(pl.k))

            if pl.highest_range_kill_m and pl.highest_range_kill_m > 0:
                crud.set_max_counter(db, uid, "highest_range_kill.m", float(pl.highest_range_kill_m))
                crud.set_max_counter_wipe(db, uid, "highest_range_kill.m", float(pl.highest_range_kill_m))

            processed += 1

        db.commit()
        return {"ok": True, "players": processed}
    except Exception:
        db.rollback()
        raise HTTPException(status_code=500, detail="ingest failed")

# ----- Admin: start new wipe -----
@router.post("/admin/wipe/start")
def admin_wipe_start(x_api_key: Optional[str] = Header(default=None),
                     db: Session = Depends(get_session)) -> Dict[str, Any]:
    if settings.API_KEY and x_api_key != settings.API_KEY:
        raise HTTPException(status_code=401, detail="invalid key")
    now = payload_time = __import__("time").time()
    crud.start_new_wipe(db, int(payload_time))
    db.commit()
    return {"ok": True, "wipe_started": crud.get_wipe_started(db)}

app.include_router(router)

