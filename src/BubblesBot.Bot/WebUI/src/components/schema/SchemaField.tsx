import type { FlaskSlot, SchemaField, SkillSlot, SlotProfile } from "../../api/types";
import { BoolSwitch } from "./editors/BoolSwitch";
import { OptionsSelect } from "./editors/OptionsSelect";
import { KeycodeButton } from "./editors/KeycodeButton";
import { NumberControl } from "./editors/NumberControl";
import { TextControl } from "./editors/TextControl";
import { StringListEditor } from "./editors/StringListEditor";
import { ModTableEditor } from "./editors/ModTableEditor";
import { SkillsEditor } from "./editors/SkillsEditor";
import { FlasksEditor } from "./editors/FlasksEditor";

interface Props {
  field: SchemaField;
  value: unknown;
  onChange: (value: unknown) => void;
}

/** Dispatches one schema field to its editor by `type`. */
export function SchemaFieldControl({ field, value, onChange }: Props) {
  switch (field.type) {
    case "bool":
      return <BoolSwitch value={!!value} onChange={onChange} />;
    case "options":
      return <OptionsSelect options={field.options ?? []} value={Number(value ?? 0)} onChange={onChange} />;
    case "keycode":
      return <KeycodeButton value={Number(value ?? 0)} onChange={onChange} />;
    case "int":
    case "float":
      return <NumberControl field={field} value={value as number | undefined} onChange={onChange} />;
    case "string":
      return <TextControl value={String(value ?? "")} onChange={onChange} />;
    case "stringlist":
      return (
        <StringListEditor
          value={Array.isArray(value) ? (value as string[]) : []}
          placeholder={field.placeholder ?? "Add entry…"}
          onChange={onChange}
        />
      );
    case "modtable":
      return (
        <ModTableEditor
          mods={field.mods ?? []}
          tiers={field.tiers ?? []}
          value={Array.isArray(value) ? (value as string[]) : []}
          onChange={onChange}
        />
      );
    case "skills":
      return <SkillsEditor value={value as SlotProfile<SkillSlot> | undefined} onChange={onChange} />;
    case "flasks":
      return <FlasksEditor value={value as SlotProfile<FlaskSlot> | undefined} onChange={onChange} />;
    default:
      return <span className="tree-empty">(unsupported field type: {field.type})</span>;
  }
}
