# Базовые образы: точные теги с digest из infra/images.lock (make check-images / make pin-images).
ARG PYTHON_IMAGE=python:3.11.17-slim-bookworm@sha256:0a310eeecf4e1f5a0743f9a6520c90c88d089c903ca5fd283f501e3a805f5f89
# Офлайн-запас: стадия-заглушка с пустыми wheels/ nuget/ npm/. compose подменяет её каталогом запаса
# (additional_contexts: offline). kaniko контекстов не знает — берёт заглушку и собирает онлайн.
FROM ${PYTHON_IMAGE} AS offline
RUN mkdir -p /wheels /nuget /npm

FROM ${PYTHON_IMAGE}
ARG OFFLINE=0
ARG MIRROR_PIP_INDEX=
WORKDIR /app
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 PIP_DISABLE_PIP_VERSION_CHECK=1 \
    DATA_DIR=/data MODEL_DIR=/models
COPY requirements.txt .
COPY --from=offline /wheels /wheels
# OFFLINE=1 — строго из запаса (лучше упасть, чем тихо пойти в сеть); иначе — запас как подсказка + PyPI/зеркало.
RUN if [ "$OFFLINE" = 1 ]; then pip install --no-cache-dir --no-index --find-links /wheels -r requirements.txt; \
    else pip install --no-cache-dir --retries 10 --timeout 60 --find-links /wheels \
         ${MIRROR_PIP_INDEX:+--index-url "$MIRROR_PIP_INDEX"} -r requirements.txt; fi \
    && rm -rf /wheels
COPY config.yaml .
COPY app ./app
EXPOSE 8000
HEALTHCHECK --interval=15s --timeout=3s CMD python -c "import urllib.request;urllib.request.urlopen('http://localhost:8000/health',timeout=2)"
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
