# 常见知名程序 / 开源组件占用的 TCP 端口速查表

> 目的：在为 Atelia 组件、本地服务、集成测试或示例程序选择端口时，快速避开高频冲突端口，降低“端口已被占用”与误连风险。
>
> 说明：本表聚焦“实践中经常撞到”的端口 + 常见开发/测试工具使用端口，并非 IANA 全集。若需要权威注册信息，请参考 IANA Service Name and Transport Protocol Port Number Registry。

## 端口区间背景
- 0–1023：Well-Known（特权）端口，通常不要自定义占用。
- 1024–49151：Registered，很多流行服务默认落在这里。
- 49152–65535：Dynamic / Ephemeral（OS 动态分配）。应用**若长期监听固定端口**，尽量避免直接挑 OS 动态区（因为调试/脚本硬编码可读性差），但**临时测试服务器**可用“端口 0 让系统自动分配”。

## 高频通用 / Web 开发端口
| 端口 | 典型程序 / 场景 | 备注 |
|------|----------------|------|
| 80 / 443 | Nginx / Apache / Caddy / 网关 | 正式 HTTP/HTTPS（勿占用自定义服务） |
| 3000 | React / Next.js / Grafana / 其他前端脚手架 | 极高冲突概率，避免自研服务默认用 |
| 3001 / 3002 | 前端第二实例 / 本地多服务 | 也常被占用 |
| 4200 | Angular CLI dev server | |
| 5000 | Flask / .NET 旧模板 / Vite(Legacy) | 常撞；.NET 8+ Kestrel 随机化但仍注意 |
| 5173 | Vite 新默认 | 前端项目普遍使用 |
| 8000 | Python SimpleHTTPServer / Django dev | 极高撞车率 |
| 8001 | 备用调试 / Django 第二实例 | |
| 8080 | 常见备用 HTTP / Tomcat / Spring Boot | 最“拥挤”之一，慎用 |
| 8081 | 备用 HTTP / Vue CLI | 仍高频 |
| 8088 | Oozie / 备用 HTTP | 大数据组件可能占用 |
| 9000 | SonarQube / MinIO 控制台 | 也用于自定义服务，注意冲突 |
| 9001 | MinIO 集群 / Node inspector (老) | |
| 9090 | Prometheus | 监控栈核心端口 |
| 9091 | Prometheus Pushgateway | |
| 9092 | Kafka Broker | |

## 数据库 / 存储 / 搜索
| 端口 | 服务 | 备注 |
|------|------|------|
| 1433 | Microsoft SQL Server | |
| 1521 | Oracle Database | |
| 2049 | NFS | 有时测试卷挂载冲突 |
| 2379 / 2380 | etcd client / peer | K8s 组件依赖 |
| 27017 | MongoDB | 常见本地占用 |
| 3306 | MySQL / MariaDB | |
| 4200 | Firebird (也见前端) | 少见但存在双重含义 |
| 5432 | PostgreSQL | 高频；PG 多实例改 5433+ |
| 5984 | CouchDB | |
| 6379 | Redis | 最常见缓存端口 |
| 6380 | Redis TLS / 第二实例 | |
| 7000-7005 | Redis Cluster slots | 集群节点范围（示例） |
| 7000/9042 | Cassandra native / 9042 CQL | 7000 (intra-node), 7001 TLS |
| 7199 | Cassandra JMX | |
| 7700 | etcd（自定义集群偶见） | 避免默认化选择 |
| 8086 | InfluxDB | |
| 8123 | ClickHouse HTTP | 9000 为其 native, 9440 TLS |
| 8500 | Consul HTTP | 8600 为 DNS |
| 8600 | Consul DNS | |
| 9000 | ClickHouse native / SonarQube / MinIO | 高冲突多含义 |
| 9042 | Cassandra CQL | |
| 9050 | Tor SOCKS | 若做安全测试可能存在 |
| 9093 | Alertmanager | 监控栈 |
| 9200 | Elasticsearch HTTP | |
| 9300 | Elasticsearch Transport | ES 集群节点通信 |
| 11211 | Memcached | |

## 消息 / 流处理 / 协调
| 端口 | 服务 | 备注 |
|------|------|------|
| 1883 | MQTT (Mosquitto 等) | 物联网场景常驻 |
| 8883 | MQTT over TLS | |
| 4222 | NATS | 8222 为监控，6222 路由 |
| 4223 | NATS 备用（冲突多时） | 避免用作自研默认 |
| 4369 | Erlang epmd (RabbitMQ 依赖) | Rabbit 集群必在 |
| 5672 | RabbitMQ AMQP | |
| 5671 | RabbitMQ AMQP TLS | |
| 15672 | RabbitMQ 管理 Web | |
| 61613 | STOMP | ActiveMQ / RabbitMQ 插件 |
| 61616 | ActiveMQ | |
| 2181 | Zookeeper | Kafka/HBase 等依赖 |
| 2888 / 3888 | Zookeeper follower / election | 集群内部 |
| 9092 | Kafka Broker | 再次提醒高频 |
| 8083 | Kafka Connect | |
| 8081 | Schema Registry (Confluent) | 与前端端口冲突潜在 |

## 监控 / 可观测 / 分布式追踪
| 端口 | 服务 | 备注 |
|------|------|------|
| 4317 | OpenTelemetry OTLP gRPC | 标准采集 |
| 4318 | OTLP HTTP | |
| 55680 / 55681 | 旧 OTLP 实验端口 | 新项目避免使用 |
| 8200 | HashiCorp Vault | |
| 8888 | Jupyter / 有时本地代理 | 冲突可能中等 |
| 9411 | Zipkin | |
| 16686 | Jaeger UI | 14268/14250 为 ingest |
| 9100 | Node Exporter | K8s 节点监控常驻 |
| 8080/8008 | Profiling / 监控端口（自定义） | 避免不加命名就抢 8080 |
| 6060 | Go pprof (默认示例) | 生产避免暴露 |

## 搜索 / 索引 / AI 相关服务
| 端口 | 服务 | 备注 |
|------|------|------|
| 7700 | Meilisearch | 若前述用过 7700 注意冲突 |
| 6333 | Qdrant | |
| 19530 | Milvus | |
| 11434 | Ollama | 本地大模型服务默认 |
| 8000 / 7860 | Gradio / Stable Diffusion UI | AI Demo 高频 |
| 8501 | Streamlit | |

## 身份认证 / 网关 / 服务发现
| 端口 | 服务 | 备注 |
|------|------|------|
| 389 / 636 | LDAP / LDAPS | 企业环境常见 |
| 5000* | Keycloak (有时) | 也可能 8080/9080 |
| 7001 | WebLogic / Cassandra TLS / 其他 | 多含义冲突 |
| 8761 | Netflix Eureka | Spring Cloud 教程常用 |
| 8848 | Nacos | |
| 5001 | ASP.NET HTTPS Dev / Keycloak alt | 浏览器信任问题需注意 |

## DevOps / 容器 / 云原生
| 端口 | 服务 | 备注 |
|------|------|------|
| 2375 / 2376 | Docker Remote API (HTTP/TLS) | 切勿在生产裸奔 2375 |
| 5000 | local Docker registry / Dev servers | 与上文冲突频繁 |
| 32000+ | K8s NodePort 默认可能范围 | 如 K8s 集群内注意避免硬编码 |
| 6443 | Kubernetes API Server | |
| 10250 | Kubelet API | |
| 10254 | Ingress-Nginx healthz | |
| 10257 / 10259 | kube-controller-manager / scheduler | |
| 15000 / 15001 | Istio Envoy 监听 | Sidecar 注入后会占用 |
| 15090 | Istio metrics | |

## 版本控制 / 构建 / CI
| 端口 | 服务 | 备注 |
|------|------|------|
| 9418 | Git 协议 (read-only) | 偶尔被防火墙阻断 |
| 29418 | Gerrit | |
| 8080 / 8081 | Jenkins / TeamCity / Drone UI | 冲突再现 |
| 9090 | Drone / Prometheus 冲突可能 | |
|
## 邮件 / 通讯 / 其他
| 端口 | 服务 | 备注 |
|------|------|------|
| 25 / 465 / 587 | SMTP | 企业或本地 mock 时需知 |
| 2525 | Mailtrap / Dev SMTP | 常用测试端口 |
| 8025 | MailHog UI | SMTP: 1025 |
| 5222 / 5269 | XMPP client / server | 即时通讯 |
| 25565 | Minecraft Server | 内网开发机偶尔撞到 (演示) |
| 50051 | gRPC 教程/示例常用端口 | 建议避免默认写死 |
| 53 | DNS | 若做内置解析服务需 root 权限（或改高端口） |

## 前端 / 调试工具补充
| 端口 | 工具 | 备注 |
|------|------|------|
| 9229 | Node.js Inspector (新) | 旧 5858 已弃用 |
| 24678 | Vite HMR WebSocket (内置) | 若代理需放行 |
| 35729 | LiveReload | 前端热刷新 |

## 常见“看似空闲却易被后来进程抢走”的端口
| 端口 | 原因 | 建议 |
|------|------|------|
| 3000 / 5173 / 8000 / 8080 | 脚手架 / 栈默认 | 避免作为后端服务长期端口 |
| 5000 | 多栈历史默认 | 自研勿默认；若必须，支持可配置 |
| 9000 | 多重含义 (Sonar / MinIO / ClickHouse) | 自研新服务避免 |
| 9090 | Prometheus 标准 | 若做业务 API 改用更高段 |
| 6379 / 5432 | Redis / PostgreSQL | 保留给真实依赖，不要挪作其他 |

## 端口选择策略建议（Atelia 内部）
1. 定义“项目自留保留段”：例如 34000–34999 分配给内部微服务（按文档登记）。
2. 集成 / 临时测试：优先使用端口 0 让 OS 分配，测试框架记录实际端口（避免硬编码）。
3. 演示 / 示例代码：使用 34xxx 段并在 README 标明，避免与开发常用前端端口冲突。
4. 不要复用已被知名外部服务使用的端口作为我们某组件的“默认端口”，除非我们的组件就是为了兼容该协议。
5. 提供环境变量覆盖机制：如 `ATELIA_<SERVICE>_PORT`；并在冲突时输出清晰错误 + 建议端口。
6. 在 CI 中可加入端口占用预检查脚本，检测是否意外监听在黑名单（如 5432/6379/9092 等）的端口。

### 内部登记（示例模板）
| 服务名 | 默认端口 | 可配置环境变量 | 是否必须固定 | 备注 |
|--------|----------|----------------|--------------|------|
| （示例）ContextIndex | 34010 | ATELIA_CONTEXT_INDEX_PORT | 否 | 可改 0 动态 |
| （示例）VectorStore  | 34011 | ATELIA_VECTOR_STORE_PORT  | 否 | |

> 建议新增服务前先搜索 `3401` 前缀确认未被占用，然后更新本文档。

## 如何检查占用 & 自动回避
- PowerShell: `Get-Process -Id (Get-NetTCPConnection -LocalPort <PORT>).OwningProcess`
- lsof (Unix): `lsof -iTCP:<PORT> -sTCP:LISTEN`
- 自适应绑定示例（伪 C# 逻辑）：
  1. 读取配置端口
  2. 若为 0 -> 直接绑定 (系统分配)
  3. 若失败 (SocketException: Address in use) 且允许回退 -> 在保留段内线性或随机探测 10 次
  4. 超过尝试仍失败 -> 终止并提示使用的冲突进程 PID

## 更新与维护
- 本表不是静态的：新增常用 AI / 数据 / 监控组件时应追加。
- 评审周期：建议每季度审视一次（新增内部服务需同步）。
- 若发现重复分配：及时在 PR 中修改“内部登记”表格并 @相关负责人。

## 参考（非复制，仅列出来源类型）
- IANA Port Registry（核对官方分配状态）
- 各开源项目官方文档（Kafka / Redis / PostgreSQL / Elasticsearch / Prometheus / Istio 等）
- 业界常见脚手架默认配置与经验汇总

---
如需新增条目或调整策略，请在 PR 中说明：用途、是否可改端口、与现有条目的差异。
