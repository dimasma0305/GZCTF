import { Alert, Anchor, Badge, Button, CopyButton, Group, Loader, Stack, Table, Text, Tooltip } from '@mantine/core'
import { mdiAlertCircleOutline, mdiContentCopy, mdiCrown, mdiRefresh } from '@mdi/js'
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
    case 'Corrupt':
      return 'red'
    default:
      return 'gray'
  }
}

// Mirrors the server-side KothHillStateModel element of GET .../Ad/Koth/Hills.
// The auto-generated Api.ts SDK doesn't pick up the new endpoint until the next
// OpenAPI regen, so we type it inline and call useSWR directly against the URL —
// same pattern as KothChallengePanel.
interface KothHillStateItem {
  challengeId: number
  title: string | null
  round: number
  holderParticipationId: number | null
  holderTeamName: string | null
  isYou: boolean
  status: string | null
  checkedAt: string | null
  lastRefreshRound: number
  ip: string | null
  port: number | null
}

interface KothHillListProps {
  gameId: number
}

/**
 * Live status of EVERY hill in the game, in one ID-free list: hill name, target
 * IP:port (copy-button), who currently holds it (highlighted when it's YOU), and
 * the functional probe verdict. Replaces the "type a challenge id and curl
 * /State" flow — players just read the list. Polls every 10s, same cadence as the
 * per-hill panel.
 */
export const KothHillList: FC<KothHillListProps> = ({ gameId }) => {
  const { t } = useTranslation()
  const { data, error, isLoading, mutate } = useSWR<KothHillStateItem[]>(
    `/api/game/${gameId}/ad/koth/hills`,
    { refreshInterval: 10_000 }
  )

  if (error) {
    return (
      <Alert
        color="red"
        variant="light"
        icon={<Icon path={mdiAlertCircleOutline} size={1} />}
        title={t('game.content.koth.hills.error_title', 'Couldn’t load hills')}
      >
        <Group justify="space-between" wrap="nowrap">
          <Text size="sm">
            {t('game.content.koth.hills.error', 'Failed to fetch the hill list. Check your session and try again.')}
          </Text>
          <Button size="compact-xs" variant="subtle" leftSection={<Icon path={mdiRefresh} size={0.7} />} onClick={() => mutate()}>
            {t('common.button.retry', 'Retry')}
          </Button>
        </Group>
      </Alert>
    )
  }

  if (isLoading && !data) {
    return (
      <Group gap="xs" c="dimmed">
        <Loader size="xs" />
        <Text size="sm">{t('game.content.koth.hills.loading', 'Loading hills…')}</Text>
      </Group>
    )
  }

  if (!data || data.length === 0) {
    return (
      <Text size="sm" c="dimmed" fs="italic">
        {t('game.content.koth.hills.empty', 'No hills yet — the game may still be in warmup, or the operator hasn’t launched the containers.')}
      </Text>
    )
  }

  return (
    <Stack gap="xs">
      <Table.ScrollContainer minWidth={520}>
        <Table verticalSpacing={6} horizontalSpacing="sm" highlightOnHover>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('game.content.koth.hills.col_hill', 'Hill')}</Table.Th>
              <Table.Th>{t('game.content.koth.hills.col_target', 'Target')}</Table.Th>
              <Table.Th>{t('game.content.koth.hills.col_holder', 'Holder')}</Table.Th>
              <Table.Th>{t('game.content.koth.hills.col_status', 'Status')}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {data.map((h) => {
              const target = h.ip ? `${h.ip}${h.port ? `:${h.port}` : ''}` : null
              return (
                <Table.Tr key={h.challengeId}>
                  <Table.Td>
                    <Text size="sm" fw={600} truncate maw={160}>
                      {h.title || `#${h.challengeId}`}
                    </Text>
                  </Table.Td>
                  <Table.Td>
                    {target ? (
                      <CopyButton value={target}>
                        {({ copied, copy }) => (
                          <Tooltip
                            label={copied ? t('game.tooltip.copy.copied', 'Copied') : t('game.tooltip.copy.target', 'Copy hill address')}
                            withArrow
                            position="top"
                          >
                            <Anchor component="button" type="button" onClick={copy} className={misc.ffmono} size="xs">
                              <Group gap={4} wrap="nowrap">
                                {target}
                                <Icon path={mdiContentCopy} size={0.55} />
                              </Group>
                            </Anchor>
                          </Tooltip>
                        )}
                      </CopyButton>
                    ) : (
                      <Text size="xs" c="dimmed">
                        {t('game.content.koth.hills.no_target', 'no container')}
                      </Text>
                    )}
                  </Table.Td>
                  <Table.Td>
                    {h.isYou ? (
                      <Badge size="sm" color="violet" variant="filled" leftSection={<Icon path={mdiCrown} size={0.5} />}>
                        {t('game.content.koth.you_hold_it', 'You hold it')}
                      </Badge>
                    ) : h.holderTeamName ? (
                      <Text size="sm" truncate maw={140}>
                        {h.holderTeamName}
                      </Text>
                    ) : (
                      <Text size="sm" c="dimmed">
                        {t('game.content.koth.hills.no_holder', 'up for grabs')}
                      </Text>
                    )}
                  </Table.Td>
                  <Table.Td>
                    <Badge size="sm" color={statusColor(h.status)} variant={h.status ? 'light' : 'outline'}>
                      {h.status ?? t('game.content.ad.no_checks_yet', 'no checks yet')}
                    </Badge>
                  </Table.Td>
                </Table.Tr>
              )
            })}
          </Table.Tbody>
        </Table>
      </Table.ScrollContainer>
      <Text size="xs" c="dimmed">
        {t(
          'game.content.koth.hills.note',
          'Updates every ~10s. “You hold it” = your team is the current holder. Plant your token (above) on any hill to take it; click a target to copy its address.'
        )}
      </Text>
    </Stack>
  )
}
