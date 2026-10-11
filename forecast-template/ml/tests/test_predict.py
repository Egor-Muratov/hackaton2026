"""Минимальный тест контракта /health и /predict (docs/contracts/ml-api.yaml).

Без БД: fetch_rows подменяется синтетикой. Тест на живой БД включается env ML_TEST_DATASET_ID
(датасет 'ml-fixture', см. README).
"""
import math
import os
from datetime import date, timedelta

import pytest
from fastapi.testclient import TestClient

from app import main

DS = "3f1c2a9e-7b1d-4c55-9a51-2b0c1d7e8f00"
CUTOFF = date(2017, 8, 15)
KEYS = [(1, "AUTOMOTIVE"), (1, "BOOKS"), (44, "GROCERY I"), (2, "AUTOMOTIVE")]


def fake_rows(s, dataset_id, start, end):
    if str(dataset_id) != DS:
        return []
    rows, d = [], start + timedelta(days=1)
    while d <= end:
        for n, (store, fam) in enumerate(KEYS):
            if d <= CUTOFF:
                sales = 0.0 if fam == "BOOKS" else 10.0 * (n + 1) * (1 + 0.3 * (d.weekday() >= 5)) + (d.day % 3)
            else:
                sales = None
            rows.append((store, fam, d, sales, 1))
        d += timedelta(days=1)
    return rows


@pytest.fixture
def client(monkeypatch):
    monkeypatch.setattr(main, "fetch_rows", fake_rows)
    return TestClient(main.app)


def body(**kw):
    b = {"dataset_id": DS, "run_id": None, "cutoff_date": "2017-08-15", "horizon": 16,
         "series_keys": None, "include_aggregate": True, "interval": 0.8}
    b.update(kw)
    return b


def test_health(client):
    r = client.get("/health")
    assert r.status_code == 200
    j = r.json()
    assert set(j) == {"status", "model_version", "model_loaded"} and j["status"] == "ok" and j["model_loaded"]


def test_predict_all(client):
    r = client.post("/predict", json=body(), headers={"X-Correlation-Id": "t-1"})
    assert r.status_code == 200, r.text
    j = r.json()
    assert set(j) == {"model_version", "dataset_id", "cutoff_date", "horizon", "interval",
                      "forecasts", "aggregate", "cv_metrics", "warnings"}
    assert len(j["forecasts"]) == len(KEYS) * 16
    dates = {(CUTOFF + timedelta(days=h)).isoformat() for h in range(1, 17)}
    per = {}
    for p in j["forecasts"]:
        assert set(p) == {"series_key", "date", "yhat", "lower", "upper"}
        assert set(p["series_key"]) == {"store_nbr", "family"} and isinstance(p["series_key"]["store_nbr"], int)
        assert 0 <= p["lower"] <= p["yhat"] <= p["upper"]
        per.setdefault((p["series_key"]["store_nbr"], p["series_key"]["family"]), set()).add(p["date"])
    assert all(v == dates for v in per.values()) and len(per) == len(KEYS)
    assert [a["date"] for a in j["aggregate"]] == sorted(dates)
    total = sum(p["yhat"] for p in j["forecasts"] if p["date"] == "2017-08-16")
    assert math.isclose(j["aggregate"][0]["yhat"], total, rel_tol=1e-6)
    assert {"rmsle", "mae", "baseline_rmsle"} <= set(j["cv_metrics"])
    assert any(w.startswith("series_all_zero: 1") for w in j["warnings"])
    books = [p for p in j["forecasts"] if p["series_key"]["family"] == "BOOKS"]
    assert all(p["yhat"] == p["lower"] == p["upper"] == 0 for p in books)


def test_predict_subset_no_aggregate(client):
    r = client.post("/predict", json=body(horizon=7, include_aggregate=False,
                                          series_keys=[{"store_nbr": 1, "family": "AUTOMOTIVE"},
                                                       {"family": "GROCERY I", "store_nbr": 44}]))
    assert r.status_code == 200, r.text
    j = r.json()
    assert j["aggregate"] is None and len(j["forecasts"]) == 14


@pytest.mark.parametrize("patch", [
    {"series_keys": []},
    {"horizon": 17},
    {"horizon": 0},
    {"interval": 1.0},
    {"extra_field": 1},
    {"dataset_id": "not-a-uuid"},
    {"dataset_id": "00000000-0000-0000-0000-000000000000"},
    {"cutoff_date": "2010-01-01"},
    {"series_keys": [{"store_nbr": 999, "family": "AUTOMOTIVE"}]},
    {"series_keys": [{"store": 1, "family": "AUTOMOTIVE"}]},
])
def test_predict_422(client, patch):
    r = client.post("/predict", json=body(**patch))
    assert r.status_code == 422, r.text
    assert "detail" in r.json()


def test_predict_missing_required(client):
    b = body()
    del b["series_keys"]
    assert client.post("/predict", json=b).status_code == 422


def test_predict_503(client, monkeypatch):
    monkeypatch.setattr(main.state, "model_loaded", False)
    r = client.post("/predict", json=body())
    assert r.status_code == 503 and "detail" in r.json()
    assert client.get("/health").json()["model_loaded"] is False


@pytest.mark.skipif(not os.environ.get("ML_TEST_DATASET_ID"), reason="ML_TEST_DATASET_ID not set")
def test_predict_live_db():
    c = TestClient(main.app)
    r = c.post("/predict", json=body(dataset_id=os.environ["ML_TEST_DATASET_ID"]))
    assert r.status_code == 200, r.text
    j = r.json()
    n = len({(p["series_key"]["store_nbr"], p["series_key"]["family"]) for p in j["forecasts"]})
    assert len(j["forecasts"]) == n * 16 and j["cv_metrics"]["rmsle"] is not None
