import { useStatusStore } from "../../state/statusStore";

const STATUS_CLASS: Record<string, string> = { Success: "good", Failure: "bad", Running: "warn" };

export function TreePanel() {
  const tree = useStatusStore((s) => s.status?.tree);
  if (!tree || tree.length === 0) return <div className="tree-empty">(no active mode)</div>;
  return (
    <div className="tree">
      {tree.map((node, i) => (
        <div className="tree-row" key={i}>
          <span className="t">{"  ".repeat(node.depth)}{node.name}</span>
          <span className={`s ${STATUS_CLASS[node.status] ?? ""}`}>{node.status}</span>
        </div>
      ))}
    </div>
  );
}
