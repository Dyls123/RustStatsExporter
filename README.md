# RustStatsExporter
REST API Service and Web Frontend for use with the Oxide/uMod Rust Stats Exporter.

1. 🎮 Oxide/uMod Plugin (RustStatsExporter.cs)

- Collects per-player statistics during gameplay:
- PvP: kills, deaths, weapon usage, projectiles fired, explosives, range of kills
- PvE: animal kills, NPC kills, barrels destroyed, Bradley/Chinook/Heli kills
- Resources: wood, stone, metal, HQM, scrap gathered
- Gambling: Big Wheel, Slots, Blackjack — spent vs. profit
- Miscellaneous: distance traveled, airdrops, hackable crates, etc.
- Samples distance regularly, tracks gambling scrap deltas
- Periodically exports stats as JSON to an external API
- Uses a configurable flush timer and distance sample interval
- Optionally requires an API key for security
- Falls back to local JSON file (RustStatsExporter_LastBatch.json) if the API is -unavailable

2. 🖥️ REST API Service (FastAPI + PostgreSQL)

- Written in Python (FastAPI, SQLAlchemy)
- Stores stats in a PostgreSQL database
- Provides endpoints for:
- POST /ingest → accepts payloads from the plugin
  - GET /keys → lists all available stat keys
  - GET /leaderboard/{key} → returns top players for a given stat
  - GET /players/search?q= → search players by name
  - GET /players/{id} → fetch all stats for a specific player
- Enforces optional X-API-Key authentication
- Persists all player counters with additive updates (upsert logic)
- Tracks highest values (e.g., long-range kill) separately

3. 🌐 Web Frontend

- Clean React/JavaScript single-page app
- Automatically connects to the API (configurable base URL)
- Provides sortable leaderboard tables
- Multiple sections:
- PvP (Kills, Deaths, Ammo usage, Rockets, etc.)
- Resources (Wood, Stone, Metal, HQM, Scrap, etc.)
- Gambling (Big Wheel, Slots, Blackjack spent/profit)
- Miscellaneous (Distance traveled, Airdrops, Hackable crates, etc.)
- Player names are displayed instead of SteamIDs
- Fully sortable by columns (highest/lowest)

✨ Why use this?

- Track player performance over time
- Create leaderboards to drive competition
- Provide public stats pages for your community
- Easy to extend with new stat keys or UI sections

🔧 Requirements

- Rust server running Oxide/uMod
- PostgreSQL database
- Docker (recommended for API + frontend deployment)

🚀 Quick Start

- Install the Oxide plugin (RustStatsExporter.cs) to your Rust server.
- Deploy the API + DB stack with docker compose up -d.
- Serve the frontend and point it to your API base URL.
- Visit the web UI and enjoy real-time leaderboards!
