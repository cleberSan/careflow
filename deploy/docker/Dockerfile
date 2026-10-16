# syntax=docker/dockerfile:1
# Imagem de um serviço do CareFlow (ADR-0013). Compila na arquitetura do runner e só copia o resultado para o
# runtime da arquitetura alvo (arm64 na VM da Oracle), sem emulação: a publicação é framework-dependent e sem RID,
# então o mesmo IL roda em x64 e arm64.
#
#   docker buildx build --platform linux/arm64 --build-arg SERVICE=Demo -f deploy/docker/Dockerfile .
#
# Runtime chiseled: sem shell, sem gerenciador de pacotes, usuário sem privilégio. A variante "extra" traz ICU e
# tzdata (o Notifications converte horários para o fuso da clínica). O healthcheck usa o próprio binário
# (--health-probe), porque não há curl nem wget na imagem.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG SERVICE
WORKDIR /src
RUN test -n "$SERVICE" || (echo "Informe --build-arg SERVICE (PatientVault, Consent, Scheduling, ...)" && exit 1)
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
RUN dotnet restore "src/Services/${SERVICE}/CareFlow.${SERVICE}.Api/CareFlow.${SERVICE}.Api.csproj" --locked-mode
RUN dotnet publish "src/Services/${SERVICE}/CareFlow.${SERVICE}.Api/CareFlow.${SERVICE}.Api.csproj" \
      -c Release --no-restore -o /app -p:UseAppHost=false
# A triagem da demonstração pública reproduz as respostas gravadas do modelo (ADR-0011, ADR-0013).
COPY eval/triage/recordings/ /recordings/
RUN mkdir -p /app/recordings && if [ "$SERVICE" = "Triage" ]; then cp /recordings/*.json /app/recordings/; fi
# Pasta de estado do Demo (registro de clínicas). Um volume novo herda dono e modo daqui; sem isso nasceria do root.
RUN mkdir -p /state

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS runtime
ARG SERVICE
ARG REVISION=desconhecida
LABEL org.opencontainers.image.source="https://github.com/cleberSan/careflow" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.title="careflow-${SERVICE}"
WORKDIR /app
COPY --from=build /app ./
# 1654 é o usuário sem privilégio das imagens chiseled (APP_UID).
COPY --from=build --chown=1654:1654 /state /var/lib/careflow-demo
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0 \
    DOTNET_GCConserveMemory=5 \
    DOTNET_USE_POLLING_FILE_WATCHER=true
EXPOSE 8080
# O comando de cada serviço (dotnet CareFlow.<Serviço>.Api.dll) vem do compose de produção.
ENTRYPOINT ["dotnet"]
