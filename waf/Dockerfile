# Multi-stage build for Rust WAF sidecar

# Stage 1: Builder
FROM golang:1.24-bookworm AS builder

WORKDIR /build

# Copy module files
COPY waf/go.mod waf/go.sum ./

# Download dependencies
RUN go mod download

# Copy source code
COPY waf/ .

# Build WAF binary
RUN CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build \
    -ldflags="-s -w" \
    -o /waf \
    ./cmd/waf/

# Stage 2: Runtime
FROM debian:bookworm-slim

# Install CA certificates for TLS
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user
RUN groupadd -r waf && useradd -r -g waf waf

WORKDIR /app

# Copy binary from builder
COPY --from=builder /waf /app/waf

# Change ownership to non-root user
RUN chown waf:waf /app/waf && chmod 755 /app/waf

# Drop privileges
USER waf

# Expose ports
EXPOSE 28015/udp 28016/tcp 28017/udp 8080/tcp 9090/tcp

# Health check using native Go binary
HEALTHCHECK --interval=30s --timeout=3s --retries=3 \
    CMD ["/app/waf", "-healthcheck"]

# Start WAF sidecar
ENTRYPOINT ["/app/waf"]
