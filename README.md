# URL Shortener System

## Architecture Overview
This system is a scalable, resilient, and secure URL shortening service built using C# and .NET 8, following a microservices architecture. The system is composed of three main services, with an Nginx load balancer and API gateway:
- **URL Service**: Handles URL shortening and redirection.
- **User Service**: Manages user authentication, quotas, and paid plans.
- **Analytics Service**: Collects and processes access analytics for shortened URLs, including geolocation via MaxMind GeoIP2.
- **Nginx**: Acts as a reverse proxy, load balancer, and API gateway with rate limiting.

## Design Decisions
1. **Microservices**: Chosen for horizontal scalability and independent deployment.
2. **Database**:
   - **PostgreSQL**: Stores URLs, user data, plans, and analytics due to its reliability and scalability.
   - **Redis**: Used for caching URLs, quotas, and geolocation data to reduce database load.
3. **Messaging**: RabbitMQ for asynchronous communication (e.g., click events).
4. **Geolocation**:
   - **MaxMind GeoIP2**: Integrated into the Analytics Service for geolocation based on client IPs, using the GeoLite2 City database.
   - Geolocation data is cached in Redis for 24 hours to optimize performance.
5. **Containerization**:
   - Docker for packaging services and dependencies.
   - Docker Compose for local orchestration, with volumes for PostgreSQL, RabbitMQ, and MaxMind data.
6. **Load Balancing and Rate Limiting**:
   - Nginx as a reverse proxy and load balancer, distributing traffic across multiple instances of each service.
   - Rate limiting applied per IP:
     - `/shorten`: 10 req/min.
     - `/analytics/*`: 30 req/min.
     - `/register` and `/login`: 5 req/min.
7. **Security**:
   - JWT for authentication.
   - Rate limiting to prevent abuse.
   - URL validation to ensure safety.
   - Passwords hashed with bcrypt.
8. **Scalability**:
   - Stateless services for easy horizontal scaling.
   - Kubernetes for orchestration and auto-scaling in production.

## Setup Instructions
1. **Prerequisites**:
   - .NET 8 SDK
   - Docker and Docker Compose
   - PostgreSQL (via Docker)
   - Redis (via Docker)
   - RabbitMQ (via Docker)
   - MaxMind GeoLite2 City database (download from https://www.maxmind.com after registering)
2. **Configuration**:
   - Create a `.env` file with the following:
     ```env
     POSTGRES_USER=postgres
     POSTGRES_PASSWORD=your_secure_password
     RABBITMQ_USER=guest
     RABBITMQ_PASSWORD=guest
     JWT_KEY=your-secure-jwt-key-here-32chars
     ```
   - Place the GeoLite2 City database in `data/maxmind/GeoLite2-City.mmdb`.
   - Update `appsettings.json` for each service with connection strings (handled automatically via Docker Compose environment variables).
   - Install NuGet packages:
     ```bash
     dotnet add package MaxMind.GeoIP2
     dotnet restore
     ```
3. **Running the Application**:
   - Build and start all services:
     ```bash
     docker-compose up --build
     ```
   - Access services via Nginx:
     - API Gateway: `http://localhost`
     - RabbitMQ Management: `http://localhost:15672` (default credentials: guest/guest)
   - Test endpoints:
     - `POST /register`: Register a new user.
     - `POST /login`: Authenticate and get a JWT.
     - `POST /shorten`: Create a shortened URL.
     - `GET /{shortCode}`: Redirect to the original URL.
     - `GET /users/{userId}/quota`: Check the user's URL quota.
     - `POST /upgrade-plan`: Simulate upgrading to a paid plan.
     - `GET /analytics/{shortCode}`: Retrieve access statistics with geolocation.
4. **Rate Limiting**:
   - Configured in Nginx:
     - `/shorten`: 10 requests per minute per IP.
     - `/analytics/*`: 30 requests per minute per IP.
     - `/register` and `/login`: 5 requests per minute per IP.
   - Exceeding limits returns HTTP 429 (Too Many Requests).

## Scaling
- Deploy services in Kubernetes with a load balancer for production.
- Use Redis for caching to reduce database load.
- Monitor performance with Prometheus and Grafana.
- Mount the MaxMind database as a volume in the Analytics Service container.
- Scale services by adding more instances in `docker-compose.yml` or Kubernetes.

## Security
- JWT authentication for all protected endpoints.
- Rate limiting at the Nginx level to prevent abuse.
- Input validation to prevent injection attacks.
- Passwords stored with bcrypt hashing.
- MaxMind database stored in a secure volume.

## Future Improvements
- Integrate a real payment gateway (e.g., Stripe) for plan upgrades.
- Add advanced analytics (e.g., device type, browser).
- Implement CI/CD pipelines for automated deployments.
- Add support for custom short codes.
- Automate periodic updates of the MaxMind GeoLite2 database.
- Configure HTTPS in Nginx for production.