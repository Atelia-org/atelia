export type JsonRpcId = number | string;

export interface JsonRpcRequest {
  id: JsonRpcId;
  method: string;
  params?: unknown;
}

export interface JsonRpcNotification {
  method: string;
  params?: unknown;
}

export interface JsonRpcResponse {
  id: JsonRpcId;
  result?: unknown;
  error?: {
    code: number;
    message: string;
    data?: unknown;
  };
}

export type JsonRpcMessage = JsonRpcRequest | JsonRpcNotification | JsonRpcResponse;

export function hasId(value: unknown): value is { id: JsonRpcId } {
  if (typeof value !== "object" || value === null || !("id" in value)) return false;
  const id = (value as { id: unknown }).id;
  return typeof id === "string" || typeof id === "number";
}

export function hasMethod(value: unknown): value is JsonRpcNotification & { id?: JsonRpcId } {
  return (
    typeof value === "object" &&
    value !== null &&
    "method" in value &&
    typeof (value as { method: unknown }).method === "string"
  );
}

