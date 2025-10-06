import os
from pydantic import BaseModel

class Settings(BaseModel):
    API_KEY: str = os.getenv("API_KEY", "")
    DB_URL: str = os.getenv(
        "DB_URL",
        "postgresql+psycopg2://rust:rust@db:5432/ruststats"
    )
    CORS_ORIGINS: str = os.getenv("CORS_ORIGINS", "*")
    RATE_LIMIT_PER_MIN: int = int(os.getenv("RATE_LIMIT_PER_MIN", "600"))

settings = Settings()
