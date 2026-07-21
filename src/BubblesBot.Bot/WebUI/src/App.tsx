import { NavLink, Navigate, Route, Routes } from "react-router-dom";
import { ConnectionPill } from "./components/ConnectionPill";
import DashboardPage from "./pages/DashboardPage";
import SettingsPage from "./pages/SettingsPage";
import StrategyListPage from "./pages/StrategyListPage";
import StrategyEditorPage from "./pages/StrategyEditorPage";
import WizardPage from "./pages/WizardPage";
import RunsPage from "./pages/RunsPage";

export default function App() {
  return (
    <>
      <header>
        <div className="header-left">
          <div className="title">BubblesBot</div>
          <nav className="topnav">
            <NavLink to="/" end>Dashboard</NavLink>
            <NavLink to="/strategies">Atlas</NavLink>
            <NavLink to="/runs">Runs</NavLink>
            <NavLink to="/settings">Settings</NavLink>
            <NavLink to="/setup">Setup</NavLink>
          </nav>
        </div>
        <ConnectionPill />
      </header>
      <main>
        <Routes>
          <Route path="/" element={<DashboardPage />} />
          <Route path="/strategies" element={<StrategyListPage />} />
          <Route path="/strategies/:id" element={<StrategyEditorPage />} />
          <Route path="/settings" element={<SettingsPage />} />
          <Route path="/setup" element={<WizardPage />} />
          <Route path="/runs" element={<RunsPage />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </main>
    </>
  );
}
