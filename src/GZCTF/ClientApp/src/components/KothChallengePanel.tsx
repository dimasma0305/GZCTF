import {
  Alert,
  Badge,
  CopyButton,
  Group,
  Loader,
  Stack,
  Text,
  Tooltip,
} from '@mantine/core'
import { mdiAlertCircleOutline, mdiCrown } from '@mdi/js'
import { Icon } from '@mdi/react'
import { FC } from 'react'
import { useTranslation } from 'react-i18next'
import useSWR from 'swr'
import misc from '@Styles/Misc.module.css'

const statusColor = (s?: string | null) => {
  switch (s) {
    case 'Ok':
      return 'teal'
    case 'Mumble':
      return 'yellow'
    case 'Offline':
      return 'red'
    case 'InternalError':
      return 'gray'
    default:
      return 'gray'
  }
}

// Shapes mirror the server-side response models; the auto-generated Api.ts
// SDK doesn't pick them up until the next OpenAPI regen, so we type them
// inline here and call useSWR directly against the URL.
interface KothTokenModel {
  round: number
  token: string | null
  status: 'warmup' | 'no-token-this-round' | 'ready'
}

interface KothHillStateModel {
  round: number
  holderParticipationId: number | null
  holderTeamName: string | null
  isYou: boolean
  status: string | null
  checkedAt: string | null
  lastRefreshRound: number
}

interface AdHillTarget {
  ip: string | null
  port: number | null
  lastCheckStatus: string | null
  lastRefreshRound: number
}

interface AdTargetsModel {
  currentRound: number
  challenges: { challengeId: number; hill?: AdHillTarget | null }[]
}

interface KothChallengePanelProps {
  gameId: number
  challengeId: number
}

/**
 * Per-challenge King of the Hill status block. Mirrors the layout of
 * <see cref="AdChallengePanel"/> but for the shared hill model:
 *   - the hill IP:port (one shared container per challenge — copy-button so the
 *     player can drop it straight into curl);
 *   - the team's control token (GAME-WIDE — one per refresh window, the same
 *     value works on every hill — copy-button to plant into /koth/king);
 *   - who's holding it right now (highlights when it's YOU);
 *   - the latest functional verdict on the hill.
 *
 * Uses useSWR with 5s polling so the holder + status update without manual
 * refresh — same cadence as the A&D panel's adState hook.
 */
export const KothChallengePanel: FC<KothChallengePanelProps> = ({ gameId, challengeId }) => {
  const { t } = useTranslation()

  // The Token endpoint requires player auth (cookie session). The token is
  // game-wide and only rotates on the refresh-window boundary, but we still
  // poll at 15s so the value reappears promptly after a reset (15s is
  // conservative; the default tick is 60s).
  const { data: tokenData } = useSWR<KothTokenModel>(
    `/api/game/${gameId}/ad/koth/${challengeId}/token`,
    { refreshInterval: 15_000 }
  )
  const { data: stateData } = useSWR<KothHillStateModel>(
    `/api/game/${gameId}/ad/koth/${challengeId}/state`,
    { refreshInterval: 10_000 }
  )
  const { data: targets } = useSWR<AdTargetsModel>(
    `/api/game/${gameId}/ad/targets`,
    { refreshInterval: 30_000 }
  )

  const hill = targets?.challenges.find((c) => c.challengeId === challengeId)?.hill ?? null

  // Loading: neither came back yet → show a single spinner so the modal
  // doesn't flash empty.
  if (!tokenData && !stateData) {
    return (
      <Group justify="center" py="md">
        <Loader size="sm" />
      </Group>
    )
  }

  return (
    <Stack gap={6}>
      {/* Hill state — who holds it right now + functional verdict */}
      <Group justify="space-between" wrap="nowrap" align="center">
        <Group gap="xs" wrap="nowrap">
          <Icon path={mdiCrown} size={0.7} color="var(--mantine-color-violet-6)" />
          <Text fw="bold" size="sm">
            {t('game.content.koth.hill', 'The hill')}
          </Text>
          <Badge
            size="sm"
            color={statusColor(hill?.lastCheckStatus ?? stateData?.status)}
            variant={(hill?.lastCheckStatus ?? stateData?.status) ? 'filled' : 'light'}
          >
            {hill?.lastCheckStatus ?? stateData?.status ?? t('game.content.ad.no_checks_yet', 'no checks yet')}
          </Badge>
        </Group>
        {stateData?.holderTeamName && (
          <Badge
            size="sm"
            color={stateData.isYou ? 'violet' : 'gray'}
            variant={stateData.isYou ? 'filled' : 'light'}
          >
            {stateData.isYou
              ? t('game.content.koth.you_hold_it', 'You hold it')
              : t('game.content.koth.holder', { team: stateData.holderTeamName, defaultValue: 'Holder: {{team}}' })}
          </Badge>
        )}
      </Group>

      {/* Hill IP:port — copy-button to drop into curl */}
      {hill?.ip && (
        <Group gap={6} align="center" wrap="nowrap">
          <Text size="xs" c="dimmed">
            {t('game.content.ad.target', 'Target')}:
          </Text>
          <CopyButton value={`${hill.ip}:${hill.port ?? ''}`}>
            {({ copied, copy }) => (
              <Tooltip
                label={
                  copied
                    ? t('game.tooltip.copy.copied', 'Copied')
                    : t('game.tooltip.copy.target', 'Copy hill address')
                }
              >
                <Text
                  className={misc.ffmono}
                  size="xs"
                  truncate
                  style={{ cursor: 'pointer' }}
                  onClick={copy}
                >
                  {hill.ip}{hill.port ? `:${hill.port}` : ''}
                </Text>
              </Tooltip>
            )}
          </CopyButton>
          {hill.lastRefreshRound > 0 && (
            <Text size="xs" c="dimmed">
              · {t('game.content.koth.last_refresh', 'last refresh: round {{round}}', { round: hill.lastRefreshRound })}
            </Text>
          )}
        </Group>
      )}

      {/* The team's current-round token — copy-button so the player can plant it.
          Distinguish warmup ("no round yet") from "we missed the mint this round"
          so the UI doesn't look broken during the gap before round 1. */}
      <Group gap={6} align="center" wrap="nowrap">
        <Text size="xs" c="dimmed">
          {tokenData
            ? `${t('game.content.koth.your_token', 'Your token (round {{round}})', { round: tokenData.round })}:`
            : `${t('game.content.koth.your_token_short', 'Your token')}:`}
        </Text>
        {/* No data yet (initial load or a failed token fetch) — show a hint rather
            than a bare label with a blank value, which looks broken. */}
        {!tokenData && (
          <Text size="xs" c="dimmed" fs="italic">
            {t('game.content.koth.token_loading', 'loading…')}
          </Text>
        )}
        {tokenData?.status === 'warmup' && (
          <Text size="xs" c="dimmed" fs="italic">
            {t('game.content.koth.warmup', 'Game hasn’t started ticking yet')}
          </Text>
        )}
        {tokenData?.status === 'no-token-this-round' && (
          <Text size="xs" c="orange" fs="italic">
            {t('game.content.koth.no_token', 'No token this round (joined late?) — should resolve next tick')}
          </Text>
        )}
        {tokenData?.status === 'ready' && tokenData.token && (
          <CopyButton value={tokenData.token}>
            {({ copied, copy }) => (
              <Tooltip
                label={
                  copied
                    ? t('game.tooltip.copy.copied', 'Copied')
                    : t('game.tooltip.copy.koth_token', 'Copy token — write into /koth/king on any hill (same token works on all of them)')
                }
              >
                <Text
                  className={misc.ffmono}
                  size="xs"
                  fw="bold"
                  truncate
                  style={{ cursor: 'pointer', maxWidth: 320 }}
                  onClick={copy}
                >
                  {tokenData.token}
                </Text>
              </Tooltip>
            )}
          </CopyButton>
        )}
      </Group>

      {/* No hill rendered yet — operator hasn't ensured containers, or the
          5-tick refresh just wiped it. Surface a hint instead of silence. */}
      {!hill?.ip && targets && (
        <Alert
          icon={<Icon path={mdiAlertCircleOutline} size={0.9} />}
          color="orange"
          variant="light"
          p="xs"
        >
          <Text size="xs">
            {t(
              'game.content.koth.no_hill',
              'Hill not running yet. If this persists, ask the operator to ensure containers.'
            )}
          </Text>
        </Alert>
      )}
    </Stack>
  )
}
