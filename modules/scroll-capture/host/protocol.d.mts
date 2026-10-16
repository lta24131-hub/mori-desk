export type CaptureAction = "copy" | "save" | "pin" | "collect";
export type CaptureAnnotation = {
    kind: "rect" | "ellipse" | "line" | "arrow" | "pen" | "text" | "mosaic";
    x1: number; y1: number; x2: number; y2: number;
    color: string; strokeWidth: number; fontSize: number;
    text?: string; points?: {x:number;y:number}[]; mosaicPath?: string;
};
export type CaptureRequest = {
    schemaVersion: 1; sessionId: string; outputDirectory: string;
    region: {x:number;y:number;width:number;height:number};
    monitor: {x:number;y:number;width:number;height:number;scaleFactor:number};
    initialAnnotations: CaptureAnnotation[];
    annotationsClip: {x:number;y:number;width:number;height:number};
    parentProcessId?: number;
};
export const SCHEMA_VERSION: number;
export const ACTIONS: readonly CaptureAction[];
export const MAX_PIXELS: number;
export const MAX_PNG_BYTES: number;
export function validateRequest(raw: unknown, requestPath?: string): CaptureRequest;
export function readRequest(requestPath: string): CaptureRequest;
export function validateAnnotations(raw: unknown, outputDirectory: string): CaptureAnnotation[];
export function validatePng(png: Buffer): {width:number;height:number};
export function writeResult(request: CaptureRequest, status: "ok"|"cancelled"|"error", options?: {action?:CaptureAction;png?:Buffer;message?:string}): object;
export function writeReady(request: CaptureRequest): object;
