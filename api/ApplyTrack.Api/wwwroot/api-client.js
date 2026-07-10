// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

export function createApiClient(onUnauthorized) {
  async function readResponse(response) {
    if (!response.ok) {
      if (response.status === 401) onUnauthorized();
      let detail = response.statusText;
      try { detail = (await response.json()).detail || detail; } catch (_) {}
      const error = new Error(detail);
      error.status = response.status;
      throw error;
    }
    if (response.status === 204) return null;
    return response.json();
  }

  async function api(method, path, body) {
    const options = { method, headers: {} };
    if (body !== undefined) {
      options.headers["Content-Type"] = "application/json";
      options.body = JSON.stringify(body);
    }
    return readResponse(await fetch(path, options));
  }

  api.form = async function form(path, body) {
    return readResponse(await fetch(path, { method: "POST", body }));
  };

  return api;
}
