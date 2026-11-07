# Production image for the Angular app: static files behind nginx, which also proxies /api/ and /hubs/ (WebSockets) to the API,
# so the browser only ever talks to one origin (cookies, CSRF and the hub's Origin check all rely on that).
# STATUS: written but NOT BUILT or run (Docker was not available to the author). Build it in CI before relying on it:
#   docker build -f docker/web.Dockerfile -t pulsereserve-web .

FROM node:24-alpine AS build
ENV NG_CLI_ANALYTICS=false CI=true
WORKDIR /app
COPY frontend/pulsereserve-web/package.json frontend/pulsereserve-web/package-lock.json ./
RUN npm ci --ignore-scripts --no-audit --no-fund
COPY frontend/pulsereserve-web/ ./
RUN npx ng build --configuration production

FROM nginxinc/nginx-unprivileged:stable-alpine AS runtime
COPY docker/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/pulsereserve-web/browser /usr/share/nginx/html
EXPOSE 8080
