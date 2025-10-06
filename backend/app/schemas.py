from pydantic import BaseModel, Field
from typing import Dict, List

class InPlayer(BaseModel):
    user_id: int
    last_name: str
    k: Dict[str, float] = Field(default_factory=dict)
    highest_range_kill_m: float = 0.0

class IngestPayload(BaseModel):
    server_unix_time: int
    players: List[InPlayer]

class PlayerOut(BaseModel):
    user_id: int
    last_name: str
    last_seen: int
    counters: Dict[str, float]
    highest_range_kill_m: float

class LeaderboardRow(BaseModel):
    user_id: int
    last_name: str
    value: float
