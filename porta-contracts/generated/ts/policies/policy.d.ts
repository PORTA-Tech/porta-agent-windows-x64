export interface RegulationPolicy {
  contract_name: "RegulationPolicy";
  schema_version: "1.0.0";
  policy_id: string;
  policy_name: string;
  version: string;
  target_state: "stable" | "elevated_risk" | "degrading" | "critical" | "recovering";
  conditions: {
    operator: "AND" | "OR";
    /**
     * @minItems 1
     * @maxItems 16
     */
    conditions:
      | [
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          }
        ]
      | [
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          }
        ]
      | [
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          }
        ]
      | [
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          }
        ]
      | [
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          }
        ]
      | [
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          }
        ]
      | [
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          }
        ]
      | [
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          }
        ]
      | [
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          }
        ]
      | [
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          }
        ]
      | [
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          }
        ]
      | [
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          }
        ]
      | [
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          }
        ]
      | [
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          }
        ]
      | [
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          }
        ]
      | [
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          },
          {
            field: "probability" | "confidence" | "baseline_deviation_factor";
            op: ">" | ">=" | "<" | "<=" | "==";
            value: number;
          }
        ];
  };
  /**
   * @minItems 1
   * @maxItems 8
   */
  interventions:
    | [
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        }
      ]
    | [
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        }
      ]
    | [
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        }
      ]
    | [
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        }
      ]
    | [
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        }
      ]
    | [
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        }
      ]
    | [
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        }
      ]
    | [
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        },
        {
          type:
            | "notify"
            | "reduce_load"
            | "restart_service"
            | "isolate_component"
            | "escalate_to_human"
            | "pause_agent";
          level: number;
          max_duration_seconds: number;
          parameters: {
            [k: string]: string | number | boolean;
          };
        }
      ];
  hysteresis_config: {
    activate_threshold: number;
    deactivate_threshold: number;
    flap_window_seconds: number;
    max_flaps_per_window: number;
  };
  cooldown_seconds: number;
  dwell_seconds: number;
  /**
   * @minItems 1
   * @maxItems 5
   */
  override_permissions:
    | ["owner" | "admin" | "operator" | "analyst" | "viewer"]
    | ["owner" | "admin" | "operator" | "analyst" | "viewer", "owner" | "admin" | "operator" | "analyst" | "viewer"]
    | [
        "owner" | "admin" | "operator" | "analyst" | "viewer",
        "owner" | "admin" | "operator" | "analyst" | "viewer",
        "owner" | "admin" | "operator" | "analyst" | "viewer"
      ]
    | [
        "owner" | "admin" | "operator" | "analyst" | "viewer",
        "owner" | "admin" | "operator" | "analyst" | "viewer",
        "owner" | "admin" | "operator" | "analyst" | "viewer",
        "owner" | "admin" | "operator" | "analyst" | "viewer"
      ]
    | [
        "owner" | "admin" | "operator" | "analyst" | "viewer",
        "owner" | "admin" | "operator" | "analyst" | "viewer",
        "owner" | "admin" | "operator" | "analyst" | "viewer",
        "owner" | "admin" | "operator" | "analyst" | "viewer",
        "owner" | "admin" | "operator" | "analyst" | "viewer"
      ];
  fleet_scope: {
    all: boolean;
    /**
     * @maxItems 5000
     */
    machine_ids: string[];
    /**
     * @maxItems 100
     */
    tags: string[];
  };
  created_by: string;
  created_at: string;
  signature: string;
}
