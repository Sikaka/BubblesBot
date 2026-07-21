import { useMemo } from "react";
import type { SchemaField, Settings } from "../../api/types";
import { fieldPath, pathGet, sameValue } from "../../lib/paths";
import { SchemaFieldControl } from "./SchemaField";

interface Props {
  fields: SchemaField[];
  values: Settings;
  /** Last-applied snapshot; per-field dirty markers compare against this. */
  saved: Settings;
  onChange: (path: string[], value: unknown) => void;
  /** Optional filter, e.g. wizard pages rendering one category subset. */
  categories?: string[];
}

/** Category-grouped form generated from the reflection schema. */
export function SchemaForm({ fields, values, saved, onChange, categories }: Props) {
  const grouped = useMemo(() => {
    const byCategory = new Map<string, SchemaField[]>();
    for (const field of fields) {
      if (categories && !categories.includes(field.category)) continue;
      const list = byCategory.get(field.category) ?? [];
      list.push(field);
      byCategory.set(field.category, list);
    }
    return byCategory;
  }, [fields, categories]);

  return (
    <div>
      {[...grouped.entries()].map(([category, categoryFields]) => (
        <div key={category}>
          <div className="section-head">{category}</div>
          {categoryFields.map((field) => {
            const path = fieldPath(field);
            const value = pathGet(values, path);
            const isDirty = !sameValue(value, pathGet(saved, path));
            return (
              <div className={`field ${isDirty ? "dirty" : ""}`} key={path.join(".")}>
                <label>{field.displayName}{isDirty && <span className="dirty-dot" title="unsaved"> ●</span>}</label>
                {field.description && <div className="desc">{field.description}</div>}
                <div className="ctl">
                  <SchemaFieldControl field={field} value={value} onChange={(v) => onChange(path, v)} />
                </div>
              </div>
            );
          })}
        </div>
      ))}
    </div>
  );
}
