# build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
RUN apk add --no-cache \
    clang \
    build-base \
    zlib-dev

WORKDIR /src
COPY . .

RUN dotnet publish TelegramToMatrixForward.csproj -c Release -r linux-musl-x64 -o /out

# runtime stage
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine
WORKDIR /app
COPY --from=build /out/TelegramToMatrixForward .
ENTRYPOINT ["./TelegramToMatrixForward"]
