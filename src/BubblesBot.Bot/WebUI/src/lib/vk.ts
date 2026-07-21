// Win32 virtual-key display names + key-capture helper (port of the vanilla dashboard's
// VK table). e.keyCode is the Win32 VK on standard keyboards in current browsers.

const VK_NAMES: Record<number, string> = {
  0x01: "LMB", 0x02: "RMB", 0x04: "MMB", 0x05: "XB1", 0x06: "XB2",
  0x08: "Backspace", 0x09: "Tab", 0x0d: "Enter", 0x10: "Shift", 0x11: "Ctrl", 0x12: "Alt",
  0x14: "CapsLock", 0x1b: "Esc", 0x20: "Space", 0x21: "PgUp", 0x22: "PgDn", 0x23: "End",
  0x24: "Home", 0x25: "Left", 0x26: "Up", 0x27: "Right", 0x28: "Down", 0x2d: "Insert",
  0x2e: "Delete",
  0xc0: "`", 0xbb: "=", 0xbd: "-", 0xdb: "[", 0xdd: "]", 0xdc: "\\",
  0xba: ";", 0xde: "'", 0xbc: ",", 0xbe: ".", 0xbf: "/",
};
for (let i = 0; i < 26; i++) VK_NAMES[0x41 + i] = String.fromCharCode(65 + i);
for (let i = 0; i < 10; i++) VK_NAMES[0x30 + i] = String.fromCharCode(48 + i);
for (let i = 1; i <= 12; i++) VK_NAMES[0x6f + i] = "F" + i; // VK_F1 = 0x70

export function vkLabel(vk: number | undefined | null): string {
  if (!vk) return "—";
  return VK_NAMES[vk] ?? `VK 0x${vk.toString(16).toUpperCase()}`;
}

/** One-shot window-level key capture; resolves with the VK of the next keydown. */
export function captureNextKey(onCapture: (vk: number) => void): () => void {
  const handler = (e: KeyboardEvent) => {
    e.preventDefault();
    e.stopPropagation();
    window.removeEventListener("keydown", handler, true);
    onCapture(e.keyCode);
  };
  window.addEventListener("keydown", handler, true);
  return () => window.removeEventListener("keydown", handler, true);
}
