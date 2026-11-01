# Crosscheck session server for Railway. Build context = repo root (the server reads
# protocol/schema, which lives outside server/). Config: railway.json at the repo root.
FROM node:22-slim

WORKDIR /app

COPY server/package.json server/package-lock.json ./server/
RUN cd server && npm ci

COPY protocol ./protocol
COPY server ./server

WORKDIR /app/server
ENV NODE_ENV=production
CMD ["npm", "start"]
