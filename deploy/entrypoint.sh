#!/bin/sh
# Railway and Render inject $PORT. Default to 8080 for plain docker runs.
PORT="${PORT:-8080}"
export ASPNETCORE_URLS="http://+:${PORT}"
exec dotnet MeisterDev.ProPR.Api.dll
