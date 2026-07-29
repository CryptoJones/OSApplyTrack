# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
"""Regression checks for the hardened production container contract."""

from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[1]


def test_production_database_is_not_published() -> None:
    compose = yaml.safe_load((ROOT / "docker-compose.production.yml").read_text())

    assert "ports" not in compose["services"]["db"]
    assert compose["networks"]["database"]["internal"] is True
    assert compose["services"]["db"]["networks"] == ["database"]


def test_production_runtimes_drop_privileges_and_write_only_to_tmpfs() -> None:
    compose = yaml.safe_load((ROOT / "docker-compose.production.yml").read_text())

    expected_users = {"api": "1654:1654", "poller": "10001:10001"}
    for name, user in expected_users.items():
        service = compose["services"][name]
        assert service["user"] == user
        assert service["read_only"] is True
        assert service["cap_drop"] == ["ALL"]
        assert service["security_opt"] == ["no-new-privileges:true"]
        assert service["tmpfs"] == ["/tmp:rw,noexec,nosuid,size=64m"]
        assert "volumes" not in service


def test_runtime_images_select_non_root_users() -> None:
    api_runtime = (ROOT / "api/ApplyTrack.Api/Dockerfile").read_text().rsplit("FROM ", 1)[1]
    poller_runtime = (ROOT / "Dockerfile.poller").read_text().rsplit("FROM ", 1)[1]

    assert "\nUSER app\n" in api_runtime
    assert "\nUSER applytrack\n" in poller_runtime
