#!/bin/bash
# Idempotent environment setup for TailSlap
# No services to start — this is a desktop app

# Restore NuGet packages
dotnet restore
