# MQ Manager

Jednoduchá webová aplikace (ASP.NET Core, .NET 10) pro práci se zprávami v IBM MQ:
čtení (browse), vkládání a mazání zpráv z fronty.

## Spuštění

1. Lokální IBM MQ (volitelné):
   ```bash
   docker compose up -d
   ```
2. Aplikace:
   ```bash
   dotnet run
   ```
   UI běží na http://localhost:5268

## Konfigurace

Sekce `MQ` v `appsettings.json` (lze přepsat proměnnými prostředí, např. `MQ__Host`, `MQ__Password`):

| Klíč | Výchozí | Popis |
|---|---|---|
| QueueManager | QM1 | Název queue manageru |
| Host / Port | localhost / 1414 | Listener |
| Channel | DEV.APP.SVRCONN | SVRCONN kanál |
| User / Password | app / – | Přihlášení (MQCSP); prázdný User = bez autentizace |
| DefaultQueue | DEV.QUEUE.1 | Fronta předvyplněná v UI |
| MaxBrowse | 100 | Max. počet zobrazených zpráv |

## REST API

| Metoda | URL | Popis |
|---|---|---|
| GET | `/api/queues/{queue}/messages?max=N` | Browse zpráv (nedestruktivně) |
| POST | `/api/queues/{queue}/messages` | Vložení zprávy `{ "body": "...", "correlationId": "hex" }` |
| DELETE | `/api/queues/{queue}/messages/{messageId}` | Smazání zprávy podle MessageId (hex) |
| DELETE | `/api/queues/{queue}/messages` | Vyprázdnění fronty |
