# Cloud_API_Reliability_Dashboard
Build a small app that checks a few configured API endpoints and shows whether they’re responding, how long they take, and how that changes over time.

## Planned MVP
- An ASP.NET Core API manages monitored endpoints and check history.
- A background service checks each endpoint on a schedule, with timeouts and basic retry handling.
- Azure SQL stores endpoint details and check results.
- A React page shows current status and a simple latency history.
- Users sign in through Microsoft Entra External ID. The React app sends JWT access tokens with API requests.
- A CI/CD pipeline deploys the application.
