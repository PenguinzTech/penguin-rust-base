.PHONY: reboot wipe force-restart helm-upgrade message save ban admin mod whitelist fps logs status help

KUBE_CONTEXT  ?= dal2-beta
NAMESPACE     ?= penguin-rust
RELEASE       ?= rust-server
RCON_PORT     ?= 28016
# RCON_HOST: auto-detected from LoadBalancer service if not set
RCON_HOST     ?=

help: ## Show available targets
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | \
	  awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-18s\033[0m %s\n", $$1, $$2}'

reboot: ## Graceful reboot: warn players (5m→2m→1m→30s→10s→5s) → save → restart  (RCON_PASSWORD=<pw> make reboot)
	@if [ -z "$(RCON_PASSWORD)" ]; then \
	  echo "ERROR: set RCON_PASSWORD before running: RCON_PASSWORD=<pw> make reboot"; \
	  exit 1; \
	fi
	MODE=reboot \
	RCON_HOST="$(RCON_HOST)" RCON_PORT=$(RCON_PORT) \
	KUBE_CONTEXT=$(KUBE_CONTEXT) NAMESPACE=$(NAMESPACE) RELEASE=$(RELEASE) \
	RCON_PASSWORD="$(RCON_PASSWORD)" \
	bash scripts/admin.sh

wipe: ## Map wipe: warn players → delete .sav files (no save) → restart  (RCON_PASSWORD=<pw> make wipe)
	@if [ -z "$(RCON_PASSWORD)" ]; then \
	  echo "ERROR: set RCON_PASSWORD before running: RCON_PASSWORD=<pw> make wipe"; \
	  exit 1; \
	fi
	MODE=wipe \
	RCON_HOST="$(RCON_HOST)" RCON_PORT=$(RCON_PORT) \
	KUBE_CONTEXT=$(KUBE_CONTEXT) NAMESPACE=$(NAMESPACE) RELEASE=$(RELEASE) \
	RCON_PASSWORD="$(RCON_PASSWORD)" \
	bash scripts/admin.sh

force-restart: ## Immediately restart the pod — no player warnings (KUBE_CONTEXT=<ctx> make force-restart)
	MODE=force-restart \
	KUBE_CONTEXT=$(KUBE_CONTEXT) NAMESPACE=$(NAMESPACE) RELEASE=$(RELEASE) \
	bash scripts/admin.sh

message: ## Broadcast an admin message to all players  (RCON_PASSWORD=<pw> MESSAGE="text" make message)
	@if [ -z "$(RCON_PASSWORD)" ]; then \
	  echo "ERROR: set RCON_PASSWORD before running: RCON_PASSWORD=<pw> MESSAGE=\"text\" make message"; \
	  exit 1; \
	fi
	MODE=message \
	RCON_HOST="$(RCON_HOST)" RCON_PORT=$(RCON_PORT) \
	RCON_PASSWORD="$(RCON_PASSWORD)" MESSAGE="$(MESSAGE)" \
	KUBE_CONTEXT=$(KUBE_CONTEXT) NAMESPACE=$(NAMESPACE) RELEASE=$(RELEASE) \
	bash scripts/admin.sh

save: ## Force a world save via RCON  (RCON_PASSWORD=<pw> make save)
	@if [ -z "$(RCON_PASSWORD)" ]; then \
	  echo "ERROR: set RCON_PASSWORD before running: RCON_PASSWORD=<pw> make save"; \
	  exit 1; \
	fi
	MODE=save \
	RCON_HOST="$(RCON_HOST)" RCON_PORT=$(RCON_PORT) \
	RCON_PASSWORD="$(RCON_PASSWORD)" \
	KUBE_CONTEXT=$(KUBE_CONTEXT) NAMESPACE=$(NAMESPACE) RELEASE=$(RELEASE) \
	bash scripts/admin.sh

ban: ## Ban a player by SteamID or name  (RCON_PASSWORD=<pw> [PLAYER=<id/name>] [REASON=<text>] make ban)
	@if [ -z "$(RCON_PASSWORD)" ]; then \
	  echo "ERROR: set RCON_PASSWORD before running: RCON_PASSWORD=<pw> make ban"; \
	  exit 1; \
	fi
	MODE=ban \
	RCON_HOST="$(RCON_HOST)" RCON_PORT=$(RCON_PORT) \
	RCON_PASSWORD="$(RCON_PASSWORD)" PLAYER="$(PLAYER)" REASON="$(REASON)" \
	KUBE_CONTEXT=$(KUBE_CONTEXT) NAMESPACE=$(NAMESPACE) RELEASE=$(RELEASE) \
	bash scripts/admin.sh

admin: ## Grant admin (ownerid) to a player  (RCON_PASSWORD=<pw> [PLAYER=<id/name>] make admin)
	@if [ -z "$(RCON_PASSWORD)" ]; then \
	  echo "ERROR: set RCON_PASSWORD before running: RCON_PASSWORD=<pw> make admin"; \
	  exit 1; \
	fi
	MODE=admin \
	RCON_HOST="$(RCON_HOST)" RCON_PORT=$(RCON_PORT) \
	RCON_PASSWORD="$(RCON_PASSWORD)" PLAYER="$(PLAYER)" \
	KUBE_CONTEXT=$(KUBE_CONTEXT) NAMESPACE=$(NAMESPACE) RELEASE=$(RELEASE) \
	bash scripts/admin.sh

mod: ## Grant moderator to a player  (RCON_PASSWORD=<pw> [PLAYER=<id/name>] make mod)
	@if [ -z "$(RCON_PASSWORD)" ]; then \
	  echo "ERROR: set RCON_PASSWORD before running: RCON_PASSWORD=<pw> make mod"; \
	  exit 1; \
	fi
	MODE=mod \
	RCON_HOST="$(RCON_HOST)" RCON_PORT=$(RCON_PORT) \
	RCON_PASSWORD="$(RCON_PASSWORD)" PLAYER="$(PLAYER)" \
	KUBE_CONTEXT=$(KUBE_CONTEXT) NAMESPACE=$(NAMESPACE) RELEASE=$(RELEASE) \
	bash scripts/admin.sh

whitelist: ## Grant whitelist.allow oxide permission  (RCON_PASSWORD=<pw> [PLAYER=<id/name>] make whitelist)
	@if [ -z "$(RCON_PASSWORD)" ]; then \
	  echo "ERROR: set RCON_PASSWORD before running: RCON_PASSWORD=<pw> make whitelist"; \
	  exit 1; \
	fi
	MODE=whitelist \
	RCON_HOST="$(RCON_HOST)" RCON_PORT=$(RCON_PORT) \
	RCON_PASSWORD="$(RCON_PASSWORD)" PLAYER="$(PLAYER)" \
	KUBE_CONTEXT=$(KUBE_CONTEXT) NAMESPACE=$(NAMESPACE) RELEASE=$(RELEASE) \
	bash scripts/admin.sh

helm-upgrade: ## Apply Helm chart changes without rebooting players  (TAG=<epoch> make helm-upgrade)
	helm upgrade $(RELEASE) ./k8s/helm/rust-server \
	  --kube-context $(KUBE_CONTEXT) \
	  --namespace $(NAMESPACE) \
	  --values k8s/helm/rust-server/values.yaml \
	  --values k8s/helm/rust-server/values-beta.yaml \
	  $(if $(TAG),--set image.tag=$(TAG),)

fps: ## Query current server FPS via RCON  (RCON_PASSWORD=<pw> make fps)
	@if [ -z "$(RCON_PASSWORD)" ]; then \
	  echo "ERROR: set RCON_PASSWORD before running: RCON_PASSWORD=<pw> make fps"; \
	  exit 1; \
	fi
	MODE=fps \
	RCON_HOST="$(RCON_HOST)" RCON_PORT=$(RCON_PORT) \
	RCON_PASSWORD="$(RCON_PASSWORD)" \
	KUBE_CONTEXT=$(KUBE_CONTEXT) NAMESPACE=$(NAMESPACE) RELEASE=$(RELEASE) \
	bash scripts/admin.sh

logs: ## Tail live server logs  (KUBE_CONTEXT=<ctx> make logs)
	kubectl --context $(KUBE_CONTEXT) logs -n $(NAMESPACE) \
	  -l "app.kubernetes.io/instance=$(RELEASE)" \
	  -c rust-server --follow --tail=100

status: ## Show pod and service status  (KUBE_CONTEXT=<ctx> make status)
	@echo "=== Pods ==="
	kubectl --context $(KUBE_CONTEXT) get pods -n $(NAMESPACE) \
	  -l "app.kubernetes.io/instance=$(RELEASE)" -o wide
	@echo ""
	@echo "=== Services ==="
	kubectl --context $(KUBE_CONTEXT) get svc -n $(NAMESPACE) \
	  -l "app.kubernetes.io/instance=$(RELEASE)"
