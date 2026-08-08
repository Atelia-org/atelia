export interface AgentReport {
  summary: string;
  findings: string[];
  changed_files: string[];
  validation: string[];
  warnings: string[];
}

export const agentReportJsonSchema = {
  type: "object",
  properties: {
    summary: {
      type: "string",
      maxLength: 6000,
      description: "Concise outcome and current state. Never include reasoning or full command output.",
    },
    findings: {
      type: "array",
      maxItems: 12,
      items: { type: "string", maxLength: 1000 },
      description: "Important findings or evidence only.",
    },
    changed_files: {
      type: "array",
      maxItems: 100,
      items: { type: "string", maxLength: 1000 },
      description: "Paths changed during this turn.",
    },
    validation: {
      type: "array",
      maxItems: 20,
      items: { type: "string", maxLength: 1000 },
      description: "Test/build/check outcomes, summarized without full logs.",
    },
    warnings: {
      type: "array",
      maxItems: 20,
      items: { type: "string", maxLength: 1000 },
      description: "Remaining uncertainty or blockers.",
    },
  },
  required: ["summary", "findings", "changed_files", "validation", "warnings"],
  additionalProperties: false,
} as const;

function strings(value: unknown, limit: number): string[] {
  return Array.isArray(value)
    ? value.filter((item): item is string => typeof item === "string").slice(0, limit)
    : [];
}

export function parseAgentReport(text: string): AgentReport | undefined {
  try {
    const value: unknown = JSON.parse(text);
    if (typeof value !== "object" || value === null || !("summary" in value)) return undefined;
    const summary = (value as { summary?: unknown }).summary;
    if (typeof summary !== "string") return undefined;
    const record = value as Record<string, unknown>;
    return {
      summary,
      findings: strings(record.findings, 12),
      changed_files: strings(record.changed_files, 100),
      validation: strings(record.validation, 20),
      warnings: strings(record.warnings, 20),
    };
  } catch {
    return undefined;
  }
}

export function formatAgentReport(report: AgentReport): string {
  const sections = [report.summary.trim()];
  if (report.findings.length > 0) {
    sections.push(`Found:\n${report.findings.map((item) => `- ${item}`).join("\n")}`);
  }
  if (report.validation.length > 0) {
    sections.push(`Validation:\n${report.validation.map((item) => `- ${item}`).join("\n")}`);
  }
  return sections.filter(Boolean).join("\n\n");
}

