FROM python:3.12-slim

WORKDIR /app

RUN pip install --no-cache-dir aiohttp

COPY proxy.py .

EXPOSE 5001 5003

CMD ["python", "proxy.py"]
