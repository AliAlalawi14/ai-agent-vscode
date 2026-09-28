/**
 * Centralized endpoint paths — single source of truth.
 * Must stay synchronized with the backend's actual routes.
 *
 * Backend routes ([Route("api/[controller]")] drops the "Controller" suffix):
 *   /api/Agent/symbols
 *   /api/Agent/files
 *   /api/Agent/revert
 *   /api/Agent/plan
 *   /api/Health
 *   /v1/chat/completions
 */

export function apiUrl(base: string) {
  const b = base.replace(/\/+$/, '')
  return {
    symbols: `${b}/api/Agent/symbols`,
    files:   `${b}/api/Agent/files`,
    revert:  `${b}/api/Agent/revert`,
    approve: `${b}/api/Agent/approve`,
    plan:    `${b}/api/Agent/plan`,
    models:  `${b}/api/Agent/models`,
    health:  `${b}/api/Health`,
    stream:  `${b}/v1/chat/completions`,
  }
}
