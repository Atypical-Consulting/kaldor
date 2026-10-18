@echo off
REM Puts `kaldor` on the PATH of Claude Code on Windows, at the same version as the hooks.
REM x-release-please-start-version
dnx --yes --version 1.5.0 Atypical.Kaldor %*
REM x-release-please-end
