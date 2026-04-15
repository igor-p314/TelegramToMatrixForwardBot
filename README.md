# Telegram to Matrix Resend Bot

Бот для пересылки сообщений из Telegram в Matrix с функцией связывания пользователей через 3-значный код.

## Описание

Бот получает сообщения из Telegram и пересылает их в Matrix. Для начала работы пользователь должен связать свои аккаунты:
1. Написать `/start` боту в Telegram → получить 3-значный код
2. Написать `!start` боту в Matrix → ввести код
3. После связки все сообщения, отправленные боту в Telegram, пересылаются в Matrix

## Возможности

- Пересылка текстовых сообщений
- Пересылка фото, документов, видео, аудио, голосовых сообщений, стикеров, видеосообщений
- Ограничение размера файлов через переменную среды
- Шифрованное хранение связей (AES-256-GCM)
- AOT-компиляция для оптимальной производительности
- Логирование через Serilog (консоль + файл)

## Требования

- .NET 10.0
- Docker (опционально)

## Переменные окружения

| Переменная | Описание | Обязательная |
|------------|----------|--------------|
| `TELEGRAM_BOT_TOKEN` | Токен Telegram-бота | Да |
| `MATRIX_HOMESERVER_URL` | URL homeserver'а Matrix | Да |
| `MATRIX_BOT_USER_LOGIN` | Логин бота в Matrix | Да |
| `MATRIX_BOT_USER_PASSWORD` | Пароль бота в Matrix | Да |
| `MATRIX_BOT_BATCH_TOKEN_PATH` | Путь к файлу для сохранения токена синхронизации Matrix | Нет (`data/token.txt`) |
| `TELEGRAM_BOT_OFFSETID_PATH` | Путь к файлу для сохранения offset_id Telegram | Нет (`data/offset.txt`) |
| `LINKS_ENCRYPTION_KEY` | Base64-encoded 32-byte ключ шифрования связей (AES-256-GCM) | Да |
| `LINKS_FILE_PATH` | Путь к файлу связей | Нет (`data/links.bin`) |
| `MAX_FILE_SIZE_MB` | Максимальный размер файлов для пересылки (МБ) | Нет (50) |
| `MATRIX_BOT_MAX_MESSAGE_AGE_MS` | Максимальный возраст сообщений Matrix для обработки (мс) | Нет (4ч = 14 400 000) |
| `MATRIX_ROOM_RETENTION` | Время хранения сообщений в комнате Matrix (мс) | Нет (1 день = 86 400 000) |
| `BOT_POLL_TIMEOUT` | Таймаут long-polling для Telegram и Matrix (мс) | Нет (30 000) |

## Сборка и запуск

### Локальный запуск

```bash
export TELEGRAM_BOT_TOKEN="your_token"
export MATRIX_HOMESERVER_URL="matrix.example.com"
export MATRIX_BOT_USER_LOGIN="bot_login"
export MATRIX_BOT_USER_PASSWORD="bot_password"
export LINKS_ENCRYPTION_KEY="your_base64_32_byte_key"

dotnet run
```

### Генерация ключа шифрования

```bash
openssl rand -base64 32
```

### Сборка Native AOT

```bash
dotnet publish -c Release -r linux-musl-x64
```

### Docker

```bash
docker build -t telegram-to-matrix .
docker run -d \
  -e TELEGRAM_BOT_TOKEN=your_token \
  -e MATRIX_HOMESERVER_URL=matrix.example.com \
  -e MATRIX_BOT_USER_LOGIN=bot_login \
  -e MATRIX_BOT_USER_PASSWORD=bot_password \
  -e LINKS_ENCRYPTION_KEY=your_base64_key \
  -e MAX_FILE_SIZE_MB=50 \
  -e BOT_POLL_TIMEOUT=30000 \
  -v /path/to/data:/data \
  telegram-to-matrix
```

## Зависимости

- [Serilog](https://serilog.net/) — логирование
- [Polly](https://github.com/App-vNext/Polly) — обработка временных ошибок

## Лицензия

См. файл [LICENSE](LICENSE).
