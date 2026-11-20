# Archive image — native inventory

The image targets Linux amd64/glibc. Review native advisories, distribution
requirements and the exact image gates before publishing; Cargo advisories
alone do not cover these components.

- OpenSSL 3.5.9 LTS, Apache-2.0: [unmodified upstream source][ssl], digest
  pinned in `src/archive/Dockerfile`, linked statically into `archive-server`.
  Upstream 3.5 support ends 2030-04-08; recheck patch releases and advisories
  at each build. The upstream native test suite is not run (`no-tests`).
- bblanchon `chromium/8138` PDFium linux-x64, [publisher source/build][pdf]:
  verify both archive and library digests. Includes the publisher's PDFium and
  third-party notices, version and build arguments. No LTS promise; verify
  Chromium/PDFium updates separately. The wrapper binds the 8125 API.
- Debian Bookworm `ffmpeg` / `ffprobe`, libx264 and native AAC: Debian packages
  and dependency copyright notices live under `/usr/share/doc` in the image.
  `dpkg-query -W` records exact installed binaries and source package versions;
  find matching Debian source packages via [Debian Sources][deb]. FFmpeg with
  libx264 is GPL-enabled. Shelling out does not remove the obligation to review
  distribution and corresponding-source requirements before publishing an image.
  No new Rust crate license exemption is granted.
- For the four approved MPL-2.0 exceptions, exact unmodified crates.io source
  archives are in `/usr/share/doc/archive-native/mpl-source` with their upstream
  notices. See `auth-dependency-audit.md` and `deny.toml` for the scoped decision.
- The .NET runtime MIT notice covers derived compatibility code, not a
  shipped .NET runtime.

[ssl]: https://github.com/openssl/openssl/releases/tag/openssl-3.5.9
[pdf]: https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium/8138
[deb]: https://sources.debian.org/
