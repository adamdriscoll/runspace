FROM ubuntu:24.04

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
       ca-certificates libicu74 libssl3t64 zlib1g libgcc-s1 libstdc++6 libgssapi-krb5-2 \
       libx11-6 libice6 libsm6 libfontconfig1 libxext6 libxrandr2 libxi6 libxcursor1 \
       libglib2.0-0t64 fonts-dejavu-core xvfb xauth python3 \
    && rm -rf /var/lib/apt/lists/*

COPY validate-linux-publish.sh /validate.sh

CMD ["bash", "-c", "set -euo pipefail; if command -v pwsh || command -v dotnet; then echo 'Validation image must not contain PowerShell or .NET.' >&2; exit 1; fi; dpkg-query -W > /results/os-packages.txt; timeout 400 xvfb-run -a bash /validate.sh /payload /results"]
