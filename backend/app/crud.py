# backend/app/crud.py
from sqlalchemy.orm import Session
from sqlalchemy import select, func, text
from typing import Dict, List, Tuple
from . import models

def upsert_player(db: Session, user_id: int, name: str, last_seen: int):
    p = db.get(models.Player, user_id)

    new_name = (name or "").strip()
    # a "name" that is just the SteamID digits
    is_just_id = new_name.isdigit() and new_name == str(user_id)
    
    if p:
        changed = False

        # Only update the stored name if we have a *better* one
        if new_name and not is_just_id and p.last_name != new_name:
            p.last_name = new_name
            changed = True

        if last_seen and last_seen > (p.last_seen or 0):
            p.last_seen = last_seen
            changed = True

        return p

    # New player: if we don't have a proper name yet, fall back to ID string
    p = models.Player(
        user_id=user_id,
        last_name=new_name or str(user_id),
        last_seen=last_seen or 0,
    )
    db.add(p)
    db.flush()
    return p


def add_counters(db: Session, user_id: int, kdict: dict) -> None:
    if not kdict:
        return
    stmt = text("""
        INSERT INTO counters (user_id, key, value)
        VALUES (:uid, :key, :val)
        ON CONFLICT (user_id, key)
        DO UPDATE SET value = counters.value + EXCLUDED.value
    """)
    uid = int(user_id)
    for k, v in kdict.items():
        db.execute(stmt, {"uid": uid, "key": str(k), "val": float(v)})

def set_max_counter(db: Session, user_id: int, key: str, val: float) -> None:
    if val is None:
        return
    stmt = text("""
        INSERT INTO counters (user_id, key, value)
        VALUES (:uid, :key, :val)
        ON CONFLICT (user_id, key)
        DO UPDATE SET value = GREATEST(counters.value, EXCLUDED.value)
    """)
    db.execute(stmt, {"uid": int(user_id), "key": str(key), "val": float(val)})

def get_player(db: Session, user_id: int):
    p = db.get(models.Player, user_id)
    if not p:
        return None
    counters = {c.key: float(c.value or 0.0) for c in p.counters}
    hrk = counters.get("highest_range_kill.m", 0.0)
    return {
        "user_id": p.user_id,
        "last_name": p.last_name,
        "last_seen": p.last_seen,
        "counters": counters,
        "highest_range_kill_m": hrk,
    }

def top_leaderboard(db: Session, key: str, limit: int = 50) -> List[Tuple[int, str, float]]:
    q = (
        db.query(models.Counter.user_id, models.Player.last_name, models.Counter.value)
        .join(models.Player, models.Player.user_id == models.Counter.user_id)
        .filter(models.Counter.key == key)
        .order_by(models.Counter.value.desc())
        .limit(limit)
    )
    return [(r[0], r[1], float(r[2] or 0.0)) for r in q.all()]

def search_players(db: Session, q: str, limit: int = 20):
    qstr = f"%{q.lower()}%"
    rows = (
        db.query(models.Player)
        .filter(func.lower(models.Player.last_name).like(qstr))
        .order_by(models.Player.last_seen.desc())
        .limit(limit)
        .all()
    )
    return [{"user_id": p.user_id, "last_name": p.last_name, "last_seen": p.last_seen} for p in rows]

def list_keys(db: Session) -> List[str]:
    rows = db.execute(select(models.Counter.key).distinct()).all()
    return [r[0] for r in rows]

def add_counters_wipe(db: Session, user_id: int, kdict: dict) -> None:
    if not kdict:
        return
    stmt = text("""
        INSERT INTO counters_wipe (user_id, key, value)
        VALUES (:uid, :key, :val)
        ON CONFLICT (user_id, key)
        DO UPDATE SET value = counters_wipe.value + EXCLUDED.value
    """)
    uid = int(user_id)
    for k, v in kdict.items():
        db.execute(stmt, {"uid": uid, "key": str(k), "val": float(v)})

def set_max_counter_wipe(db: Session, user_id: int, key: str, val: float) -> None:
    if val is None:
        return
    stmt = text("""
        INSERT INTO counters_wipe (user_id, key, value)
        VALUES (:uid, :key, :val)
        ON CONFLICT (user_id, key)
        DO UPDATE SET value = GREATEST(counters_wipe.value, EXCLUDED.value)
    """)
    db.execute(stmt, {"uid": int(user_id), "key": str(key), "val": float(val)})

def top_leaderboard(db: Session, key: str, limit: int = 50, scope: str = "wipe") -> List[Tuple[int, str, float]]:
    table = models.CounterWipe if scope == "wipe" else models.Counter
    q = (
        db.query(table.user_id, models.Player.last_name, table.value)
        .join(models.Player, models.Player.user_id == table.user_id)
        .filter(table.key == key)
        .order_by(table.value.desc())
        .limit(limit)
    )
    return [(r[0], r[1], float(r[2] or 0.0)) for r in q.all()]

def list_keys(db: Session, scope: str = "wipe") -> List[str]:
    table = models.CounterWipe if scope == "wipe" else models.Counter
    rows = db.execute(select(table.key).distinct()).all()
    return [r[0] for r in rows]

# --- wipe state ---
def get_wipe_started(db: Session) -> int:
    ws = db.get(models.WipeState, 1)
    return int(ws.started_at) if ws else 0

def start_new_wipe(db: Session, started_at: int) -> None:
    # clear current wipe counters + set timestamp
    db.execute(text("TRUNCATE TABLE counters_wipe"))
    ws = db.get(models.WipeState, 1)
    if not ws:
        ws = models.WipeState(id=1, started_at=started_at)
        db.add(ws)
    else:
        ws.started_at = started_at
