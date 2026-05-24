import {
  Alert,
  Badge,
  Box,
  Button,
  Card,
  Center,
  CopyButton,
  Group,
  Loader,
  Paper,
  ScrollArea,
  SimpleGrid,
  Stack,
  Switch,
  Table,
  Text,
  Title,
  Tooltip,
} from '@mantine/core'
import { showNotification } from '@mantine/notifications'
import {
  mdiAlertCircleOutline,
  mdiCheck,
  mdiClose,
  mdiContentCopy,
  mdiPlayCircle,
  mdiRefresh,
  mdiRestart,
  mdiSwordCross,
} from '@mdi/js'
import { Icon } from '@mdi/react'
import dayjs from 'dayjs'
import { FC, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useParams } from 'react-router'
import { WithGameEditTab } from '@Components/admin/WithGameEditTab'
import { showErrorMsg } from '@Utils/Shared'
import { useAdminAdState } from '@Hooks/useGame'
import api, { AdTeamCellModel } from '@Api'

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

const AdOps: FC = () => {
  const { id } = useParams()
  const numId = parseInt(id ?? '-1', 10)
  const { t } = useTranslation()
  const { adminAdState: state, error, mutate } = useAdminAdState(numId)
  const [busy, setBusy] = useState(false)

  const isLoading = !state && !error

  const advanceRound = async () => {
    setBusy(true)
    try {
      const { data } = await api.edit.editAdvanceRound(numId)
      showNotification({
        color: 'teal',
        icon: <Icon path={mdiCheck} size={1} />,
        title: t('admin.notification.ad_ops.round_advanced.title', 'Round advanced'),
        message: t('admin.notification.ad_ops.round_advanced.message', {
          round: data.roundNumber,
          flags: data.flagsPlanted,
          defaultValue: 'Round {{round}} started — {{flags}} flags planted.',
        }),
      })
      mutate()
    } catch (e) {
      showErrorMsg(e, t)
    } finally {
      setBusy(false)
    }
  }

  const ensureContainers = async () => {
    setBusy(true)
    try {
      await api.edit.editAdEnsureContainers(numId)
      showNotification({
        color: 'teal',
        icon: <Icon path={mdiCheck} size={1} />,
        title: t('admin.notification.ad_ops.ensure_queued.title', 'Container reconcile queued'),
        message: t('admin.notification.ad_ops.ensure_queued.message',
          'Missing A&D containers will spin up shortly.'),
      })
      setTimeout(() => mutate(), 3_000)
    } catch (e) {
      showErrorMsg(e, t)
    } finally {
      setBusy(false)
    }
  }

  const toggleChallenge = async (challengeId: number) => {
    try {
      await api.edit.editAdToggleChallenge(numId, challengeId)
      mutate()
    } catch (e) {
      showErrorMsg(e, t)
    }
  }

  const restartCell = async (cell: AdTeamCellModel) => {
    try {
      await api.edit.editAdForceRestart(numId, cell.adTeamServiceId)
      showNotification({
        color: 'teal',
        icon: <Icon path={mdiRestart} size={1} />,
        title: t('admin.notification.ad_ops.restart_queued.title', 'Restart queued'),
        message: t('admin.notification.ad_ops.restart_queued.message',
          'Container will restart in seconds.'),
      })
      setTimeout(() => mutate(), 3_000)
    } catch (e) {
      showErrorMsg(e, t)
    }
  }

  if (isLoading) {
    return (
      <WithGameEditTab isLoading>
        <Center h="40vh">
          <Loader />
        </Center>
      </WithGameEditTab>
    )
  }

  if (!state || state.challenges.length === 0) {
    return (
      <WithGameEditTab>
        <Center h="40vh">
          <Stack align="center" gap="xs">
            <Icon path={mdiSwordCross} size={2.5} color="var(--mantine-color-dimmed)" />
            <Text fw="bold" c="dimmed">
              {t('admin.content.ad_ops.empty.title', 'No A&D challenges in this game')}
            </Text>
            <Text size="sm" c="dimmed">
              {t('admin.content.ad_ops.empty.description',
                'Add a challenge with type Attack & Defense to use this console.')}
            </Text>
          </Stack>
        </Center>
      </WithGameEditTab>
    )
  }

  const roundEndsIn =
    state.roundEndsAt ? Math.max(0, dayjs(state.roundEndsAt).diff(dayjs(), 'second')) : null

  return (
    <WithGameEditTab>
      <Stack gap="md">
        {/* Top status bar */}
        <Paper p="md" withBorder>
          <Group justify="space-between" align="center" wrap="nowrap">
            <Group gap="xl" wrap="nowrap">
              <Stack gap={0}>
                <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
                  {t('admin.content.ad_ops.current_round', 'Round')}
                </Text>
                <Text fw="bold" size="xl">
                  {state.currentRound ?? '—'}
                </Text>
              </Stack>
              <Stack gap={0}>
                <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
                  {t('admin.content.ad_ops.round_ends', 'Round ends')}
                </Text>
                <Text fw="bold" size="md">
                  {roundEndsIn === null
                    ? '—'
                    : roundEndsIn === 0
                      ? t('admin.content.ad_ops.round_ended', 'Round ended')
                      : `${roundEndsIn}s`}
                </Text>
              </Stack>
              <Stack gap={0}>
                <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
                  {t('admin.content.ad_ops.challenges_active', 'Challenges')}
                </Text>
                <Text fw="bold" size="md">
                  {state.challenges.filter((c) => c.isEnabled).length}/{state.challenges.length}
                </Text>
              </Stack>
            </Group>
            <Group gap="sm" wrap="nowrap">
              <Button
                leftSection={<Icon path={mdiRefresh} size={0.9} />}
                variant="default"
                size="sm"
                disabled={busy}
                onClick={() => mutate()}
              >
                {t('admin.button.ad_ops.refresh', 'Refresh')}
              </Button>
              <Button
                leftSection={<Icon path={mdiPlayCircle} size={0.9} />}
                variant="default"
                size="sm"
                disabled={busy}
                onClick={ensureContainers}
              >
                {t('admin.button.ad_ops.ensure_containers', 'Ensure containers')}
              </Button>
              <Button
                leftSection={<Icon path={mdiPlayCircle} size={0.9} />}
                size="sm"
                color="red"
                loading={busy}
                onClick={advanceRound}
              >
                {t('admin.button.ad_ops.advance_round', 'Advance round')}
              </Button>
            </Group>
          </Group>
        </Paper>

        {/* Per-challenge enable/disable cards */}
        <SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }}>
          {state.challenges.map((c) => (
            <Card key={c.challengeId} withBorder p="sm">
              <Group justify="space-between" wrap="nowrap" align="flex-start">
                <Stack gap={2} miw={0}>
                  <Text truncate fw="bold">
                    {c.title}
                  </Text>
                  <Text size="xs" c="dimmed">
                    tick {c.tickSeconds}s · lifetime {c.flagLifetimeTicks} ticks
                  </Text>
                  <Text size="xs" c="dimmed">
                    {t('admin.content.ad_ops.teams_with_container', {
                      count: c.teamsWithLiveContainer ?? 0,
                      defaultValue: '{{count}} live container(s)',
                    })}
                  </Text>
                </Stack>
                <Tooltip
                  label={
                    c.isEnabled
                      ? t('admin.tooltip.ad_ops.disable_challenge', 'Disable scoring + flag rotation')
                      : t('admin.tooltip.ad_ops.enable_challenge', 'Re-enable scoring + flag rotation')
                  }
                >
                  <Switch
                    checked={c.isEnabled}
                    onChange={() => toggleChallenge(c.challengeId)}
                    disabled={busy}
                  />
                </Tooltip>
              </Group>
            </Card>
          ))}
        </SimpleGrid>

        {/* Team × challenge grid */}
        <Paper p="md" withBorder>
          <Group justify="space-between" mb="sm">
            <Title order={4}>{t('admin.content.ad_ops.grid_title', 'Team status')}</Title>
            <Text size="xs" c="dimmed">
              {t('admin.content.ad_ops.grid_legend',
                'Click a cell IP to copy. Restart bypasses the player cooldown.')}
            </Text>
          </Group>

          {state.teams.length === 0 ? (
            <Alert color="orange" icon={<Icon path={mdiAlertCircleOutline} size={1} />}>
              {t('admin.content.ad_ops.no_teams',
                'No accepted teams yet. Once you accept teams from the participations page, their containers will spin up automatically.')}
            </Alert>
          ) : (
            <ScrollArea>
              <Table verticalSpacing="xs" striped highlightOnHover withColumnBorders>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th style={{ position: 'sticky', left: 0, zIndex: 1 }}>
                      {t('admin.content.ad_ops.column_team', 'Team')}
                    </Table.Th>
                    {state.challenges.map((c) => (
                      <Table.Th key={c.challengeId}>
                        <Text truncate fw="bold" size="sm">
                          {c.title}
                        </Text>
                      </Table.Th>
                    ))}
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {state.teams.map((row) => (
                    <Table.Tr key={row.participationId}>
                      <Table.Td
                        style={{ position: 'sticky', left: 0, zIndex: 1, background: 'var(--mantine-color-body)' }}
                      >
                        <Text truncate fw="bold" size="sm">
                          {row.teamName}
                        </Text>
                      </Table.Td>
                      {state.challenges.map((c) => {
                        const cell = row.services.find((s) => s.challengeId === c.challengeId)
                        return (
                          <Table.Td key={c.challengeId}>
                            {cell ? (
                              <Stack gap={4}>
                                <Group gap={4} wrap="nowrap">
                                  <Badge
                                    size="xs"
                                    color={statusColor(cell.lastCheckStatus)}
                                    variant={cell.lastCheckStatus ? 'filled' : 'light'}
                                  >
                                    {cell.lastCheckStatus ?? '—'}
                                  </Badge>
                                  {cell.containerIp && (
                                    <CopyButton value={`${cell.containerIp}:${cell.containerPort ?? ''}`}>
                                      {({ copied, copy }) => (
                                        <Tooltip label={copied ? 'Copied' : 'Copy IP:port'}>
                                          <Text
                                            ff="monospace"
                                            size="xs"
                                            style={{ cursor: 'pointer' }}
                                            onClick={copy}
                                          >
                                            {cell.containerIp}:{cell.containerPort}
                                          </Text>
                                        </Tooltip>
                                      )}
                                    </CopyButton>
                                  )}
                                </Group>
                                {cell.currentFlag && (
                                  <CopyButton value={cell.currentFlag}>
                                    {({ copied, copy }) => (
                                      <Tooltip
                                        label={copied ? 'Copied' : t('admin.tooltip.ad_ops.copy_flag', 'Copy current flag')}
                                      >
                                        <Text
                                          ff="monospace"
                                          size="xs"
                                          c="dimmed"
                                          truncate
                                          maw="14rem"
                                          style={{ cursor: 'pointer' }}
                                          onClick={copy}
                                        >
                                          {cell.currentFlag}
                                        </Text>
                                      </Tooltip>
                                    )}
                                  </CopyButton>
                                )}
                                <Button
                                  size="compact-xs"
                                  variant="subtle"
                                  leftSection={<Icon path={mdiRestart} size={0.7} />}
                                  onClick={() => restartCell(cell)}
                                >
                                  {t('admin.button.ad_ops.restart', 'Restart')}
                                </Button>
                              </Stack>
                            ) : (
                              <Center>
                                <Icon path={mdiClose} size={0.8} color="var(--mantine-color-dimmed)" />
                              </Center>
                            )}
                          </Table.Td>
                        )
                      })}
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </ScrollArea>
          )}
        </Paper>
      </Stack>
    </WithGameEditTab>
  )
}

export default AdOps
