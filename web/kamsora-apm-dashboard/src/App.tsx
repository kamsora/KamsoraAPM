import { Navigate, Route, Routes } from 'react-router-dom';
import { useAuth } from './auth/AuthContext';
import { TimeRangeProvider } from './components/TimeRangePicker';
import AppShell from './layout/AppShell';
import LoginPage from './pages/LoginPage';
import AcceptInvitePage from './pages/AcceptInvitePage';
import OverviewPage from './pages/OverviewPage';
import TracesPage from './pages/TracesPage';
import TraceDetailPage from './pages/TraceDetailPage';
import ServicesPage from './pages/ServicesPage';
import HostsPage from './pages/HostsPage';
import PlatformPage from './pages/PlatformPage';
import ApiKeysPage from './pages/ApiKeysPage';
import InvitesPage from './pages/InvitesPage';
import AuditLogPage from './pages/AuditLogPage';
import ChangePasswordPage from './pages/ChangePasswordPage';
import ConsumersPage from './pages/ConsumersPage';
import ConsumerDetailPage from './pages/ConsumerDetailPage';
import ErrorsPage from './pages/ErrorsPage';
import AlertRulesPage from './pages/AlertRulesPage';
import AlertChannelsPage from './pages/AlertChannelsPage';
import AlertHistoryPage from './pages/AlertHistoryPage';
import LogsPage from './pages/LogsPage';
import MetricsPage from './pages/MetricsPage';
import ServiceMapPage from './pages/ServiceMapPage';

export default function App() {
  return (
    <Routes>
      <Route path="/login"          element={<LoginPage />} />
      <Route path="/accept-invite"  element={<AcceptInvitePage />} />
      <Route element={<RequireAuth><TimeRangeProvider><AppShell /></TimeRangeProvider></RequireAuth>}>
        <Route path="/"                   element={<OverviewPage />} />
        <Route path="/traces"             element={<TracesPage />} />
        <Route path="/traces/:traceId"    element={<TraceDetailPage />} />
        <Route path="/services"           element={<ServicesPage />} />
        <Route path="/service-map"        element={<ServiceMapPage />} />
        <Route path="/hosts"              element={<HostsPage />} />
        <Route path="/consumers"          element={<ConsumersPage />} />
        <Route path="/consumers/:consumerId" element={<ConsumerDetailPage />} />
        <Route path="/errors"             element={<ErrorsPage />} />
        <Route path="/logs"               element={<LogsPage />} />
        <Route path="/metrics"            element={<MetricsPage />} />
        <Route path="/api-keys"           element={<ApiKeysPage />} />
        <Route path="/invites"            element={<InvitesPage />} />
        <Route path="/audit-log"          element={<AuditLogPage />} />
        <Route path="/alerts/rules"       element={<AlertRulesPage />} />
        <Route path="/alerts/channels"    element={<AlertChannelsPage />} />
        <Route path="/alerts/history"     element={<AlertHistoryPage />} />
        <Route path="/account/password"   element={<ChangePasswordPage />} />
        <Route path="/platform"           element={<PlatformPage />} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}

function RequireAuth({ children }: { children: React.ReactNode }) {
  const { isAuthenticated } = useAuth();
  if (!isAuthenticated) return <Navigate to="/login" replace />;
  return <>{children}</>;
}
