import { useStatusStore } from "../state/statusStore";

const releasesPage = "https://github.com/Sikaka/BubblesBot/releases/latest";

export function UpdateNotice() {
  const update = useStatusStore((state) => state.status?.update);
  if (!update?.updateAvailable) return null;

  return (
    <section className="update-notice" role="status">
      <div>
        <strong>Update available: {update.latestVersion ?? "new release"}</strong>
        <span>You are running {update.currentVersion}. Update before reporting unexpected behavior.</span>
      </div>
      <a
        href={update.releaseUrl ?? releasesPage}
        target="_blank"
        rel="noreferrer"
      >
        Download from GitHub
      </a>
    </section>
  );
}
