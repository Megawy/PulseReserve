# Development image for the Angular app: Node + `ng serve`. Source is bind-mounted by compose.dev.yaml.
FROM node:24-alpine

ENV NG_CLI_ANALYTICS=false \
    CI=false
WORKDIR /app
COPY docker/web-entrypoint.sh /usr/local/bin/web-entrypoint.sh
RUN chmod +x /usr/local/bin/web-entrypoint.sh
EXPOSE 4200
ENTRYPOINT ["/usr/local/bin/web-entrypoint.sh"]
