FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY *.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data
ENV LINKS_FILE_PATH=/data/links.bin
VOLUME ["/data"]
ENTRYPOINT ["./TelegramToMatrixForward"]
