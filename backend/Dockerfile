# backend/Dockerfile
FROM python:3.11-slim

# Prevent Python from writing .pyc files and buffering stdout/stderr
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

WORKDIR /app

# System deps (optional but nice: runtime SSL, tzdata)
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates tzdata \
 && rm -rf /var/lib/apt/lists/*

# Install Python deps
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Copy app source
COPY app ./app

# Expose API port
EXPOSE 8000

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s CMD \
  python -c "import urllib.request,urllib.error,socket,sys; \
url='http://127.0.0.1:8000/health'; \
try: \
    r=urllib.request.urlopen(url, timeout=3); sys.exit(0 if r.getcode()==200 else 1) \
except Exception: \
    sys.exit(1)"

# Start the API
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
