// Mirrors KamsoraAPM.Dashboard.Api DTOs. Keep in sync with
// src/KamsoraAPM.Dashboard.Api/Endpoints/*Endpoints.cs.

export interface LoginResponse {
  accessToken: string;
  tenantId: string;
  tenantSlug: string;
  role: string;
  isPlatformAdmin: boolean;
}

// ---- M4.1 admin / API key DTOs ----

export interface TenantSummary {
  sysTenantUuid: string;
  tenantName: string;
  tenantSlug: string;
  planType: string;
  dataRetentionDays: number;
  status: string;
  contactEmail: string;
  createdAtUtc: string;
  userCount: number;
  apiKeyCount: number;
}

export interface CreateTenantRequest {
  TenantName: string;
  TenantSlug: string;
  OwnerEmail: string;
  PlanType?: string;
  RetentionDays?: number;
  ContactEmail?: string;
}

export interface CreateTenantResponse {
  tenantUuid: string;
  tenantSlug: string;
  ownerEmail: string;
  ownerTempPassword: string;
  ingestApiKey: string;
}

export interface ApiKeySummary {
  sysApiKeyUuid: string;
  keyName: string;
  keyPrefix: string;
  scopes: string;
  expiresAtUtc: string | null;
  lastUsedAtUtc: string | null;
  createdAtUtc: string;
  createdBy: string;
}

export interface MintApiKeyRequest {
  KeyName: string;
  Scopes?: string;
  ExpiresAt?: string;
}

export interface MintApiKeyResponse {
  sysApiKeyUuid: string;
  keyPrefix: string;
  cleartext: string;
}

// ---- M4.2 invites / audit / self-service DTOs ----

export interface InviteSummary {
  sysInviteUuid: string;
  email: string;
  role: string;
  tokenPrefix: string;
  expiresAtUtc: string;
  acceptedAtUtc: string | null;
  revokedAtUtc: string | null;
  createdAtUtc: string;
  createdBy: string;
  status: 'pending' | 'accepted' | 'revoked' | 'expired';
}

export interface CreateInviteRequest {
  Email: string;
  Role?: string;
}

export interface CreateInviteResponse {
  sysInviteUuid: string;
  email: string;
  role: string;
  token: string;
  expiresAtUtc: string;
}

export interface InvitePreview {
  tenantSlug: string;
  tenantName: string;
  email: string;
  role: string;
  expiresAtUtc: string;
}

export interface AcceptInviteRequest {
  Token: string;
  Password: string;
  DisplayName?: string;
}

export interface AcceptInviteResponse {
  accessToken: string;
  tenantId: string;
  tenantSlug: string;
  role: string;
  isPlatformAdmin: boolean;
}

export interface ChangePasswordRequest {
  OldPassword: string;
  NewPassword: string;
}

export interface AuditLogEntry {
  sysAuditTransId: string;
  sysTenantUuid: string;
  actorUserUuid: string | null;
  actorEmail: string | null;
  action: string;
  targetKind: string | null;
  targetUuid: string | null;
  clientIp: string | null;
  userAgent: string | null;
  afterJson: string | null;
  postedAtUtc: string;
  postedBy: string | null;
}

export interface AuditLogPage {
  items: AuditLogEntry[];
  total: number;
  page: number;
  pageSize: number;
}

// ---- M6 consumer + error analytics DTOs ----

export interface ConsumerSummary {
  consumerId: string;
  requestCount: number;
  errorCount: number;
  errorRate: number;
  clientErrorCount: number;   // 4xx
  serverErrorCount: number;   // 5xx
  latencyP50Ms: number;
  latencyP90Ms: number;
  latencyP99Ms: number;
  distinctRoutes: number;
  firstSeenUtc: string;
  lastSeenUtc: string;
}

export interface ConsumerTimeseriesPoint {
  bucketStartUtc: string;
  requestCount: number;
  errorCount: number;
  clientErrorCount: number;
  serverErrorCount: number;
  latencyP50Ms: number;
  latencyP90Ms: number;
  latencyP99Ms: number;
}

export interface ConsumerRouteSummary {
  serviceName: string;
  httpRoute: string;
  requestCount: number;
  errorCount: number;
  errorRate: number;
  latencyP50Ms: number;
  latencyP90Ms: number;
  latencyP99Ms: number;
}

export interface RouteStatusSummary {
  serviceName: string;
  httpRoute: string;
  requestCount: number;
  status2xx: number;
  status3xx: number;
  status4xx: number;
  status5xx: number;
  errorRate: number;
  latencyP50Ms: number;
  latencyP99Ms: number;
}

export interface StatusCodeBucket {
  httpStatusCode: number;
  requestCount: number;
  latencyP50Ms: number;
  latencyP99Ms: number;
}

export interface ConsumerRouteWithSparkline {
  serviceName: string;
  httpRoute: string;
  requestCount: number;
  errorCount: number;
  errorRate: number;
  latencyP50Ms: number;
  latencyP99Ms: number;
  sparkline: number[];  // one entry per hour bucket across the window
}

// ---- M7 alerting DTOs ----

export type AlertSignalType =
  | 'latency_p50' | 'latency_p90' | 'latency_p99' | 'error_rate' | 'request_volume'
  | 'log_count' | 'metric_avg' | 'metric_max';
export type AlertOperator   = 'gt' | 'gte' | 'lt' | 'lte' | 'eq';
export type AlertSeverity   = 'info' | 'warning' | 'critical';
export type AlertState      = 'ok' | 'pending' | 'firing';
export type ChannelType     = 'webhook' | 'inapp';

export interface AlertRuleDto {
  sysRuleTransId: string;
  ruleName: string;
  description: string | null;
  enabled: boolean;
  signalType: AlertSignalType;
  /** log_count: severity floor (TRACE..FATAL). metric_avg/max: metric name. */
  signalParam: string | null;
  serviceFilter: string | null;
  operator: AlertOperator;
  threshold: number;
  windowSeconds: number;
  forSeconds: number;
  severity: AlertSeverity;
  channelUuids: string[];
  lastState: AlertState;
  lastPendingAtUtc: string | null;
  lastValue: number | null;
}

export interface CreateAlertRuleRequest {
  RuleName: string;
  Description?: string;
  SignalType: AlertSignalType;
  SignalParam?: string;
  ServiceFilter?: string;
  Operator: AlertOperator;
  Threshold: number;
  WindowSeconds: number;
  ForSeconds: number;
  Severity: AlertSeverity;
  ChannelUuids?: string[];
}

export interface UpdateAlertRuleRequest extends CreateAlertRuleRequest {
  Enabled: boolean;
}

export interface AlertChannelDto {
  sysChannelUuid: string;
  channelName: string;
  channelType: ChannelType;
  configJson: string;
  enabled: boolean;
}

export interface CreateAlertChannelRequest {
  ChannelName: string;
  ChannelType: ChannelType;
  ConfigJson?: string;
}

export interface AlertFiringDto {
  sysFiringTransId: string;
  sysRuleTransId: string;
  ruleName: string;
  signalType: AlertSignalType;
  firedAtUtc: string;
  resolvedAtUtc: string | null;
  observedValue: number;
  severity: AlertSeverity;
}

export interface AlertFiringPage {
  items: AlertFiringDto[];
  total: number;
  page: number;
  pageSize: number;
}

export interface InAppNotificationDto {
  sysNotificationTransId: string;
  sysRuleTransId: string;
  title: string;
  body: string;
  severity: AlertSeverity;
  observedValue: number;
  threshold: number;
  ruleSignal: AlertSignalType;
  acknowledgedAtUtc: string | null;
  postedAtUtc: string;
}

// ---- M8 logs + metrics DTOs ----

export interface LogRowDto {
  timestampUtc: string;
  serviceName: string;
  severityNumber: number;
  severityText: string;
  body: string;
  traceIdHex: string;
  spanIdHex: string;
  attributes: Record<string, string>;
}

export interface LogVolumePoint {
  bucketStartUtc: string;
  severityText: string;
  logCount: number;
}

export interface LogListResponse {
  items: LogRowDto[];
  nextCursorTimeUnixNano: number | null;
}

export interface MetricCatalogEntry {
  metricName: string;
  metricKind: string;        // GAUGE | SUM | HISTOGRAM
  metricUnit: string;
  serviceName: string;
  lastSeenUtc: string;
  pointCount: number;
}

export interface MetricSeriesPoint {
  bucketStartUtc: string;
  seriesKey: string;
  valueLast: number;
  valueMax: number;
  valueMin: number;
}

// ---- M11 service map DTOs ----

export interface ServiceMapNode {
  id: string;             // "svc:checkout" | "db:postgresql" | "ext:api.stripe.com"
  label: string;
  kind: 'service' | 'database' | 'external';
  callCount: number;
  errorCount: number;
  latencyP50Ms: number;
}

export interface ServiceMapEdge {
  sourceId: string;
  targetId: string;
  callCount: number;
  errorCount: number;
  avgLatencyMs: number;
}

export interface ServiceMapResult {
  nodes: ServiceMapNode[];
  edges: ServiceMapEdge[];
}

export interface OverviewSnapshot {
  totalSpans: number;
  errorSpans: number;
  errorRate: number;
  latencyP50Ms: number;
  latencyP90Ms: number;
  latencyP99Ms: number;
  distinctServices: number;
}

export interface ServiceSummary {
  serviceName: string;
  serviceVersion: string;
  spanCount: number;
  errorCount: number;
  errorRate: number;
  latencyP50Ms: number;
  latencyP90Ms: number;
  latencyP99Ms: number;
  lastSeenUtc: string;
}

export interface TimeseriesPoint {
  bucketStartUtc: string;
  count: number;
  errorCount: number;
  latencyP50Ms: number;
  latencyP90Ms: number;
  latencyP99Ms: number;
}

export interface TopRoute {
  serviceName: string;
  spanName: string;
  httpMethod: string;
  httpRoute: string;
  count: number;
  errorCount: number;
  latencyP50Ms: number;
  latencyP90Ms: number;
  latencyP99Ms: number;
}

/** Fixed-length per-bucket counts for one service / consumer (table sparklines). */
export interface EntitySparkline {
  key: string;
  counts: number[];
  errors: number[];
}

export interface HistogramBucket {
  fromMs: number;
  toMs: number;
  count: number;
}

export interface RouteDetail {
  serviceName: string;
  httpMethod: string;
  httpRoute: string;
  count: number;
  errorCount: number;
  errorRate: number;
  requestsPerMinute: number;
  latencyP50Ms: number;
  latencyP75Ms: number;
  latencyP95Ms: number;
  latencyP99Ms: number;
  timeseries: TimeseriesPoint[];
  histogram: HistogramBucket[];
}

export interface DatabaseOverview {
  totalQueries: number;
  errorQueries: number;
  errorRate: number;
  latencyP50Ms: number;
  latencyP90Ms: number;
  latencyP99Ms: number;
  totalDbTimeMs: number;
  distinctSystems: number;
}

export interface TopQuery {
  dbSystem: string;
  statement: string;
  count: number;
  errorCount: number;
  latencyP50Ms: number;
  latencyP90Ms: number;
  latencyP99Ms: number;
  totalDbTimeMs: number;
}

export interface DbSystemBreakdown {
  dbSystem: string;
  count: number;
  latencyP50Ms: number;
  latencyP99Ms: number;
}

export interface HostSummary {
  hostId: string;
  hostName: string;
  osType: string;
  osVersion: string;
  logicalCores: number;
  cpuUtilization: number;     // 0..1, latest sample
  memTotalBytes: number;
  memUsedBytes: number;
  memUtilization: number;     // 0..1, latest sample
  lastSeenUtc: string;
  sampleCount: number;
}

export interface HostUtilizationPoint {
  bucketStartUtc: string;
  cpuUserAvg: number;         // 0..1
  cpuUserMax: number;         // 0..1
  memUsedBytesAvg: number;
  memUsedBytesMax: number;
  memTotalBytes: number;
}

export interface HostDiskPoint {
  bucketStartUtc: string;
  device: string;
  totalBytes: number;
  usedBytes: number;
  readBytesPerSecAvg: number;
  writeBytesPerSecAvg: number;
  readBytesPerSecMax: number;
  writeBytesPerSecMax: number;
  readsPerSecAvg: number;
  writesPerSecAvg: number;
}

export interface HostNetworkPoint {
  bucketStartUtc: string;
  interfaceName: string;
  rxBytesPerSecAvg: number;
  txBytesPerSecAvg: number;
  rxBytesPerSecMax: number;
  txBytesPerSecMax: number;
  rxPacketsPerSecAvg: number;
  txPacketsPerSecAvg: number;
}

export interface HostProcessSummary {
  latestSampleUtc: string;
  pid: number;
  command: string;
  serviceName: string;
  runtimeVersion: string;
  cpuUtilization: number;     // 0..1
  rssBytes: number;
  threadCount: number;
  handleCount: number;
}

export interface SpanRowDto {
  traceId: string;
  spanId: string;
  parentSpanId: string;
  startTimeUnixNano: number;
  endTimeUnixNano: number;
  durationNanos: number;
  serviceName: string;
  serviceVersion: string;
  spanName: string;
  spanKind: string;
  statusCode: string;
  statusMessage: string;
  httpMethod: string;
  httpStatusCode: number;
  httpRoute: string;
  httpUrl: string;
  httpClientIp: string;
  consumerId: string;
  dbSystem: string;
  dbStatement: string;
  dbDurationNs: number;
  attributes: Record<string, string>;
  events: SpanEventDto[];
}

export interface SpanEventDto {
  name: string;
  timeUnixNano: number;
  attributesJson: string;
}

export interface TraceListResponse {
  items: SpanRowDto[];
  nextCursorNs: number | null;
}
