export type CaptureRect = { x: number; y: number; width: number; height: number };
export function layoutCaptureControls(options: { selection: CaptureRect; workArea: CaptureRect; imageWidth?: number; imageHeight?: number }): {
    toolbar: CaptureRect; preview: CaptureRect; previewSide: "right" | "left" | "inside"; scale: number;
};
