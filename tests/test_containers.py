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

    expected_users = {"api": "1654:1654", "agent": "1654:1654", "poller": "10001:10001"}
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


def test_production_agent_worker_is_the_api_image_with_no_published_port() -> None:
    compose = yaml.safe_load((ROOT / "docker-compose.production.yml").read_text())

    agent = compose["services"]["agent"]
    assert agent["image"] == compose["services"]["api"]["image"]
    assert agent["environment"]["Agent__Enabled"] == "true"
    assert "ports" not in agent


def test_production_browser_is_isolated_and_the_proxy_is_its_only_way_out() -> None:
    compose = yaml.safe_load((ROOT / "docker-compose.production.yml").read_text())

    assert compose["networks"]["browser"]["internal"] is True
    browser = compose["services"]["browser"]
    assert browser["networks"] == ["browser"]          # nothing but the proxy
    assert "ports" not in browser
    assert browser["environment"]["HTTPS_PROXY"] == "http://proxy:3128"
    assert any(opt.startswith("seccomp:") for opt in browser["security_opt"])
    # Deliberately NOT the hardened profile: Chromium cannot run read-only/noexec.
    assert "read_only" not in browser

    proxy = compose["services"]["proxy"]
    assert set(proxy["networks"]) == {"browser", "egress"}
    assert "ports" not in proxy

    agent = compose["services"]["agent"]
    assert "browser" in agent["networks"]
    assert agent["environment"]["Migrations__Mode"] == "wait"
    assert "Username=applytrack_agent" in agent["environment"]["ConnectionStrings__Postgres"]


def test_squid_refuses_private_destinations_and_non_web_ports() -> None:
    conf = (ROOT / "docker/proxy/squid.conf").read_text()
    private = (
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8",
        "169.254.0.0/16", "100.64.0.0/10", "fc00::/7",
    )
    for cidr in private:
        assert f"acl private_dst dst {cidr}" in conf
    assert "http_access deny private_dst" in conf
    assert "http_access deny CONNECT !SSL_ports" in conf
    assert "http_access deny !Safe_ports" in conf
