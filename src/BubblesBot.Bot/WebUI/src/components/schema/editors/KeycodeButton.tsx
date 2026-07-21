import { useEffect, useRef, useState } from "react";
import { captureNextKey, vkLabel } from "../../../lib/vk";

interface Props {
  value: number;
  onChange: (vk: number) => void;
}

/** Click → capture the next keydown as a Win32 VK. */
export function KeycodeButton({ value, onChange }: Props) {
  const [capturing, setCapturing] = useState(false);
  const cancelRef = useRef<(() => void) | null>(null);

  useEffect(() => () => cancelRef.current?.(), []);

  const beginCapture = () => {
    setCapturing(true);
    cancelRef.current = captureNextKey((vk) => {
      setCapturing(false);
      cancelRef.current = null;
      onChange(vk);
    });
  };

  return (
    <button type="button" className={`key-btn ${capturing ? "capturing" : ""}`} onClick={beginCapture}>
      {capturing ? "press a key…" : vkLabel(value)}
    </button>
  );
}
