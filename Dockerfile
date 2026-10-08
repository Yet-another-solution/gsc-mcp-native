FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS build
WORKDIR /src
COPY src/Gsc/Gsc.csproj Gsc/
COPY src/Gsc.Mcp/Gsc.Mcp.csproj Gsc.Mcp/
RUN dotnet restore Gsc.Mcp -r linux-$(dpkg --print-architecture | sed s/amd64/x64/)
COPY src/Gsc Gsc
COPY src/Gsc.Mcp Gsc.Mcp
RUN dotnet publish Gsc.Mcp -c Release --no-restore -r linux-$(dpkg --print-architecture | sed s/amd64/x64/) -o /app \
    && rm -f /app/*.dbg

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
COPY --from=build /app/gsc-mcp /gsc-mcp
ENTRYPOINT ["/gsc-mcp"]
