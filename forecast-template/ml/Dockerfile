FROM python:3.11-slim
WORKDIR /app
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 PIP_DISABLE_PIP_VERSION_CHECK=1 \
    DATA_DIR=/data MODEL_DIR=/models
COPY requirements.txt .
# Офлайн: скопируйте колёса в ml/wheels/ (cp ../../offline/wheels/*.whl wheels/); пустая папка (.keep) — норма.
# Если колёс нет или их не хватает — fallback на PyPI.
COPY wheels/ /wheels/
RUN (pip install --no-cache-dir --no-index --find-links /wheels -r requirements.txt \
     || pip install --no-cache-dir --find-links /wheels -r requirements.txt) \
    && rm -rf /wheels
COPY config.yaml .
COPY app ./app
EXPOSE 8000
HEALTHCHECK --interval=15s --timeout=3s CMD python -c "import urllib.request;urllib.request.urlopen('http://localhost:8000/health',timeout=2)"
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
