# Android beta releases

This directory is mounted read-only by nginx at `/downloads/` on the VPS.

APK files and their SHA-256 sidecars are deployment artifacts and are intentionally ignored by Git. The API publishes a release only when the Android update manifest in the VPS `.env` contains a valid HTTPS download URL, version code, and version string.
