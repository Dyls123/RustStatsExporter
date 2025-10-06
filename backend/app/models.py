from sqlalchemy import Column, BigInteger, String, Float, Integer, ForeignKey
from sqlalchemy.orm import relationship
from .database import Base

class Player(Base):
    __tablename__ = "players"
    user_id = Column(BigInteger, primary_key=True, index=True)
    last_name = Column(String(64), nullable=False)
    last_seen = Column(Integer, nullable=False)  # unix time
    counters = relationship("Counter", back_populates="player", cascade="all, delete-orphan")

class Counter(Base):
    __tablename__ = "counters"
    user_id = Column(BigInteger, ForeignKey("players.user_id", ondelete="CASCADE"), primary_key=True)
    key = Column(String(64), primary_key=True)
    value = Column(Float, nullable=False, default=0.0)
    player = relationship("Player", back_populates="counters")

class CounterWipe(Base):
    __tablename__ = "counters_wipe"
    user_id = Column(BigInteger, ForeignKey("players.user_id", ondelete="CASCADE"), primary_key=True)
    key = Column(String(64), primary_key=True)
    value = Column(Float, nullable=False, default=0.0)
    # optional: relationship if you want backref (not required)
    player = relationship("Player")

class WipeState(Base):
    __tablename__ = "wipe_state"
    id = Column(Integer, primary_key=True, default=1)
    started_at = Column(Integer, nullable=False, default=0)  # unix time of current wipe
