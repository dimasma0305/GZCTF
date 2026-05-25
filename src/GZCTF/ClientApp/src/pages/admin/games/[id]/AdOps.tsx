import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Center,
  Code,
  CopyButton,
  Divider,
  Group,
  Indicator,
  Loader,
  Modal,
  Paper,
  RingProgress,
  ScrollArea,
  Stack,
  Table,
  Text,
  TextInput,
  ThemeIcon,
  Title,
  Tooltip,
} from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import { showNotification } from '@mantine/notifications'
import {
  mdiAlertCircle,
  mdiAlertCircleOutline,
  mdiCheck,
  mdiCheckCircle,
  mdiClose,
  mdiCloseCircle,
  mdiDownload,
  mdiFileTree,
  mdiHelpCircle,
  mdiMagnify,
  mdiPauseCircleOutline,
  mdiPlayCircle,
  mdiRefresh,
  mdiRestart,
  mdiSwordCross,
} from '@mdi/js'
import { Icon } from '@mdi/react'
import dayjs from 'dayjs'
import { FC, useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useParams } from 'react-router'
import { WithGameEditTab } from '@Components/admin/WithGameEditTab'
import { showErrorMsg } from '@Utils/Shared'
import { useIsMobile } from '@Utils/ThemeOverride'
import { useAdminAdState } from '@Hooks/useGame'
import { useTicker } from '@Hooks/useTicker'
import api, { AdSnapshotChange, AdTeamCellModel } from '@Api'
import misc from '@Styles/Misc.module.css'
import tableClasses from '@Styles/AdOpsTable.module.css'

// Maps an A&D check status to its color, icon and short label — the single
// source of truth for how a service's health reads across the whole console.
const statusMeta = (s?: string | null): { color: string; icon: string; label: string } => {
  switch (s) {
    case 'Ok':
      return { color: 'teal', icon: mdiCheckCircle, label: 'Ok' }
    case 'Mumble':
      return { color: 'yellow', icon: mdiAlertCircle, label: 'Mumble' }
    case 'Offline':
      return { color: 'red', icon: mdiCloseCircle, label: 'Offline' }
    case 'InternalError':
      return { color: 'gray', icon: mdiHelpCircle, label: 'Error' }
    default:
      return { color: 'gray', icon: mdiHelpCircle, label: '—' }
  }
}

// docker diff kind: 0 = modified (C), 1 = added (A), 2 = deleted (D)
const kindMeta = (kind: number): { color: string; label: string } => {
  switch (kind) {
    case 1:
      return { color: 'teal', label: 'A' }
    case 2:
      return { color: 'red', label: 'D' }
    default:
      return { color: 'yellow', label: 'M' }
  }
}

interface SnapTarget {
  cell: AdTeamCellModel
  teamName: string
  challengeTitle: string
}

const SnapshotModal: FC<{ gameId: number; target: SnapTarget | null; onClose: () => void }> = ({
  gameId,
  target,
  onClose,
}) => {
  const { t } = useTranslation()
  const [loading, setLoading] = useState(false)
  const [changes, setChanges] = useState<AdSnapshotChange[]>([])
  const sid = target?.cell.adTeamServiceId

  useEffect(() => {
    if (sid === undefined) return
    let cancelled = false
    setLoading(true)
    setChanges([])
    api.edit
      .editAdSnapshotChanges(gameId, sid)
      .then(({ data }) => {
        if (!cancelled) setChanges(data.changes ?? [])
      })
      .catch(() => {
        // changes are best-effort; the tarball download is the source of truth
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [gameId, sid])

  const downloadUrl = target ? api.edit.editAdSnapshotUrl(gameId, target.cell.adTeamServiceId) : '#'
  const filename = target
    ? `ad-snapshot-team${target.cell.adTeamServiceId}-challenge${target.cell.challengeId}.tar.gz`
    : 'snapshot.tar.gz'

  const recipe = [
    `# 1. Load the team's committed container image`,
    `docker load -i ${filename}`,
    `#    → note the printed "Loaded image: <repo:tag>"`,
    ``,
    `# 2. Shell in to inspect what they shipped`,
    `docker run --rm -it <loaded-image> sh`,
    ``,
    `# 3. Diff their files against the original challenge image`,
    `#    (the list above is docker diff; for content diffs:)`,
    `docker run --rm <loaded-image> cat /path/from/list/above`,
  ].join('\n')

  return (
    <Modal
      opened={target !== null}
      onClose={onClose}
      size="xl"
      title={
        target
          ? t('admin.content.ad_ops.snapshot.title', {
              team: target.teamName,
              challenge: target.challengeTitle,
              defaultValue: 'Snapshot — {{team}} · {{challenge}}',
            })
          : ''
      }
    >
      <Stack gap="md">
        <Group justify="space-between" wrap="wrap" gap="sm">
          <Button
            component="a"
            href={downloadUrl}
            download={filename}
            leftSection={<Icon path={mdiDownload} size={0.9} />}
          >
            {t('admin.button.ad_ops.snapshot.download', 'Download .tar.gz')}
          </Button>
          <Text size="sm" c="dimmed">
            {t('admin.content.ad_ops.snapshot.changed_count', {
              count: changes.length,
              defaultValue: '{{count}} file(s) changed vs original image',
            })}
          </Text>
        </Group>

        <Divider
          label={t('admin.content.ad_ops.snapshot.diff_label', 'Filesystem changes (docker diff)')}
          labelPosition="left"
        />
        {loading ? (
          <Center h={120}>
            <Loader size="sm" />
          </Center>
        ) : changes.length === 0 ? (
          <Text size="sm" c="dimmed">
            {t('admin.content.ad_ops.snapshot.no_changes',
              'No filesystem changes were recorded (snapshot may predate diff capture, or nothing changed).')}
          </Text>
        ) : (
          <ScrollArea h={300} type="auto">
            <Stack gap={2}>
              {changes.map((ch, i) => {
                const m = kindMeta(ch.kind)
                return (
                  <Group key={`${ch.path}-${i}`} gap="xs" wrap="nowrap">
                    <Badge size="xs" color={m.color} variant="filled" w={22} p={0}>
                      {m.label}
                    </Badge>
                    <Text className={misc.ffmono} size="xs" style={{ wordBreak: 'break-all' }}>
                      {ch.path}
                    </Text>
                  </Group>
                )
              })}
            </Stack>
          </ScrollArea>
        )}

        <Divider
          label={t('admin.content.ad_ops.snapshot.inspect_label', 'Inspect locally')}
          labelPosition="left"
        />
        <Code block>{recipe}</Code>
      </Stack>
    </Modal>
  )
}

// One health-summary chip: an icon + count for a single status, dimmed when zero.
const HealthChip: FC<{ icon: string; color: string; count: number; label: string }> = ({
  icon,
  color,
  count,
  label,
}) => (
  <Tooltip label={label} withArrow>
    <Group gap={4} align="center" wrap="nowrap" style={{ opacity: count ? 1 : 0.45 }}>
      <ThemeIcon size="sm" radius="xl" variant="light" color={color}>
        <Icon path={icon} size={0.62} />
      </ThemeIcon>
      <Text fw={700} size="sm">
        {count}
      </Text>
    </Group>
  </Tooltip>
)

const AdOps: FC = () => {
  const { id } = useParams()
  const numId = parseInt(id ?? '-1', 10)
  const { t } = useTranslation()
  const { adminAdState: state, error, mutate } = useAdminAdState(numId)
  const [busy, setBusy] = useState(false)
  const [snapTarget, setSnapTarget] = useState<SnapTarget | null>(null)
  const [search, setSearch] = useState('')
  const [debouncedSearch] = useDebouncedValue(search, 200)
  const now = useTicker()
  const isMobile = useIsMobile(1080)

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
    state.roundEndsAt ? Math.max(0, dayjs(state.roundEndsAt).diff(now, 'second')) : null
  const roundTotal =
    state.roundStartedAt && state.roundEndsAt
      ? Math.max(1, dayjs(state.roundEndsAt).diff(state.roundStartedAt, 'second'))
      : null
  const roundPct =
    roundTotal && roundEndsIn !== null
      ? Math.min(100, Math.max(3, ((roundTotal - roundEndsIn) / roundTotal) * 100))
      : 0
  const ringColor =
    roundEndsIn === 0
      ? 'red'
      : roundTotal && roundEndsIn !== null && roundEndsIn / roundTotal < 0.25
        ? 'orange'
        : 'teal'
  const ringLabel =
    state.currentRound == null
      ? '—'
      : roundEndsIn === null
        ? '∞'
        : roundEndsIn === 0
          ? t('admin.content.ad_ops.round_ended_short', 'end')
          : `${roundEndsIn}s`

  // Aggregate every (team × challenge) cell into a fleet-wide health summary.
  const counts = { Ok: 0, Mumble: 0, Offline: 0, InternalError: 0, unchecked: 0 }
  state.teams.forEach((r) =>
    r.services.forEach((c) => {
      const k = c.lastCheckStatus
      if (k === 'Ok' || k === 'Mumble' || k === 'Offline' || k === 'InternalError') counts[k]++
      else counts.unchecked++
    })
  )

  const enabledChallenges = state.challenges.filter((c) => c.isEnabled).length
  const visibleTeams = state.teams.filter(
    (r) => debouncedSearch === '' || r.teamName.toLowerCase().includes(debouncedSearch.toLowerCase())
  )

  return (
    <WithGameEditTab>
      <SnapshotModal gameId={numId} target={snapTarget} onClose={() => setSnapTarget(null)} />
      <Stack gap="md">
        {/* Mission-control bar: round timing, scoring state, fleet health, actions */}
        <Paper p="md" withBorder radius="md">
          <Group justify="space-between" align="center" wrap="wrap" gap="lg">
            <Group gap="xl" wrap="wrap" align="center">
              {/* Round progress ring + number */}
              <Group gap="sm" wrap="nowrap" align="center">
                <RingProgress
                  size={76}
                  thickness={8}
                  roundCaps
                  sections={[{ value: roundPct, color: ringColor }]}
                  label={
                    <Text ta="center" fw={700} size="sm" c={ringColor === 'teal' ? undefined : ringColor}>
                      {ringLabel}
                    </Text>
                  }
                />
                <Stack gap={2}>
                  <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
                    {t('admin.content.ad_ops.current_round', 'Round')}
                  </Text>
                  <Group gap={6} align="center" wrap="nowrap">
                    <Text fw="bold" size="xl" lh={1}>
                      {state.currentRound ?? '—'}
                    </Text>
                    {state.scoringPaused ? (
                      <Badge
                        color="orange"
                        variant="light"
                        leftSection={<Icon path={mdiPauseCircleOutline} size={0.6} />}
                      >
                        {t('admin.content.ad_ops.scoring_paused', 'Scoring paused')}
                      </Badge>
                    ) : (
                      roundEndsIn !== 0 &&
                      state.currentRound != null && (
                        <Badge color="teal" variant="dot">
                          {t('admin.content.ad_ops.live', 'Live')}
                        </Badge>
                      )
                    )}
                  </Group>
                </Stack>
              </Group>

              {/* Challenges enabled */}
              <Stack gap={2}>
                <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
                  {t('admin.content.ad_ops.challenges_active', 'Challenges')}
                </Text>
                <Text fw="bold" size="xl" lh={1}>
                  {enabledChallenges}/{state.challenges.length}
                </Text>
              </Stack>

              {/* Flag cycle — game-global tick + lifetime */}
              <Stack gap={2}>
                <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
                  {t('admin.content.ad_ops.flag_cycle', 'Flag cycle')}
                </Text>
                <Text fw={600} size="sm" lh={1.3}>
                  {t('admin.content.ad_ops.tick_summary', {
                    tick: state.challenges[0].tickSeconds,
                    lifetime: state.challenges[0].flagLifetimeTicks,
                    defaultValue: 'tick {{tick}}s · lifetime {{lifetime}} ticks',
                  })}
                </Text>
              </Stack>

              {/* Fleet-wide service health */}
              <Stack gap={4}>
                <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
                  {t('admin.content.ad_ops.service_health', 'Service health')}
                </Text>
                <Group gap="md" wrap="nowrap">
                  <HealthChip icon={mdiCheckCircle} color="teal" count={counts.Ok} label="Ok" />
                  <HealthChip icon={mdiAlertCircle} color="yellow" count={counts.Mumble} label="Mumble" />
                  <HealthChip icon={mdiCloseCircle} color="red" count={counts.Offline} label="Offline" />
                  <HealthChip icon={mdiHelpCircle} color="gray" count={counts.InternalError} label="Error" />
                  {counts.unchecked > 0 && (
                    <HealthChip
                      icon={mdiHelpCircle}
                      color="dark"
                      count={counts.unchecked}
                      label={t('admin.content.ad_ops.health_unchecked', 'Unchecked')}
                    />
                  )}
                </Group>
              </Stack>
            </Group>

            <Group gap="sm" wrap="wrap" justify={isMobile ? 'flex-end' : undefined}>
              <Button
                leftSection={<Icon path={mdiRefresh} size={0.9} />}
                variant="default"
                size={isMobile ? 'xs' : 'sm'}
                disabled={busy}
                onClick={() => mutate()}
              >
                {t('admin.button.ad_ops.refresh', 'Refresh')}
              </Button>
              <Button
                leftSection={<Icon path={mdiPlayCircle} size={0.9} />}
                variant="default"
                size={isMobile ? 'xs' : 'sm'}
                disabled={busy}
                onClick={ensureContainers}
              >
                {t('admin.button.ad_ops.ensure_containers', 'Ensure containers')}
              </Button>
              <Button
                leftSection={<Icon path={mdiPlayCircle} size={0.9} />}
                size={isMobile ? 'xs' : 'sm'}
                color="red"
                loading={busy}
                onClick={advanceRound}
              >
                {t('admin.button.ad_ops.advance_round', 'Advance round')}
              </Button>
            </Group>
          </Group>
        </Paper>

        {/* Team × challenge grid */}
        <Paper p="md" withBorder radius="md">
          <Group justify="space-between" mb="sm" wrap="wrap" gap="sm">
            <Group gap="xs" align="center">
              <Title order={4}>{t('admin.content.ad_ops.grid_title', 'Team status')}</Title>
              <Badge variant="light" color="gray">
                {t('admin.content.ad_ops.teams_count', {
                  count: visibleTeams.length,
                  defaultValue: '{{count}} teams',
                })}
              </Badge>
            </Group>
            <TextInput
              size="xs"
              w={260}
              maw="100%"
              leftSection={<Icon path={mdiMagnify} size={0.8} />}
              placeholder={t('admin.placeholder.ad_ops.search_team', 'Filter teams…')}
              value={search}
              onChange={(e) => setSearch(e.currentTarget.value)}
            />
          </Group>

          {state.teams.length === 0 ? (
            <Alert color="orange" icon={<Icon path={mdiAlertCircleOutline} size={1} />}>
              {t('admin.content.ad_ops.no_teams',
                'No accepted teams yet. Once you accept teams from the participations page, their containers will spin up automatically.')}
            </Alert>
          ) : (
            <ScrollArea h="55vh" type="auto">
              <Table verticalSpacing="xs" striped highlightOnHover withColumnBorders>
                <Table.Thead className={tableClasses.thead}>
                  <Table.Tr>
                    <Table.Th className={tableClasses.corner}>
                      {t('admin.content.ad_ops.column_team', 'Team')}
                    </Table.Th>
                    {state.challenges.map((c) => (
                      <Table.Th key={c.challengeId}>
                        <Group gap={6} wrap="nowrap" justify="space-between">
                          <Text
                            truncate
                            fw="bold"
                            size="sm"
                            c={c.isEnabled ? undefined : 'dimmed'}
                            style={{ flex: 1, minWidth: 0 }}
                          >
                            {c.title}
                          </Text>
                          {c.isEnabled ? (
                            <Tooltip
                              label={t('admin.content.ad_ops.teams_with_container', {
                                count: c.teamsWithLiveContainer ?? 0,
                                defaultValue: '{{count}} live',
                              })}
                              withArrow
                            >
                              <Badge
                                size="xs"
                                variant="light"
                                color={c.teamsWithLiveContainer ? 'blue' : 'gray'}
                              >
                                {c.teamsWithLiveContainer ?? 0}
                              </Badge>
                            </Tooltip>
                          ) : (
                            <Tooltip
                              label={t('admin.tooltip.ad_ops.challenge_off',
                                'Disabled on the Challenges page — no scoring or flag rotation')}
                              withArrow
                            >
                              <Badge size="xs" variant="light" color="gray">
                                {t('admin.content.ad_ops.challenge_off', 'off')}
                              </Badge>
                            </Tooltip>
                          )}
                        </Group>
                      </Table.Th>
                    ))}
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {visibleTeams.map((row) => (
                    <Table.Tr key={row.participationId}>
                      <Table.Td className={tableClasses.left}>
                        <Text truncate fw="bold" size="sm" maw="12rem">
                          {row.teamName}
                        </Text>
                      </Table.Td>
                      {state.challenges.map((c) => {
                        const cell = row.services.find((s) => s.challengeId === c.challengeId)
                        const sm = statusMeta(cell?.lastCheckStatus)
                        return (
                          <Table.Td key={c.challengeId}>
                            {cell ? (
                              <Stack gap={6}>
                                <Group justify="space-between" wrap="nowrap" gap={4}>
                                  <Badge
                                    size="sm"
                                    color={sm.color}
                                    variant={cell.lastCheckStatus ? 'light' : 'outline'}
                                    leftSection={<Icon path={sm.icon} size={0.55} />}
                                  >
                                    {cell.lastCheckStatus ?? '—'}
                                  </Badge>
                                  <Group gap={2} wrap="nowrap">
                                    <Tooltip
                                      label={t('admin.tooltip.ad_ops.restart',
                                        'Restart container (bypasses player cooldown)')}
                                      withArrow
                                    >
                                      <ActionIcon
                                        size="sm"
                                        variant="subtle"
                                        color="gray"
                                        onClick={() => restartCell(cell)}
                                      >
                                        <Icon path={mdiRestart} size={0.7} />
                                      </ActionIcon>
                                    </Tooltip>
                                    {cell.snapshotAvailable && (
                                      <Tooltip
                                        label={t('admin.tooltip.ad_ops.snapshot',
                                          'Inspect post-game snapshot')}
                                        withArrow
                                      >
                                        <Indicator
                                          disabled={cell.changedFileCount == null}
                                          label={cell.changedFileCount}
                                          size={15}
                                          color="grape"
                                          offset={3}
                                        >
                                          <ActionIcon
                                            size="sm"
                                            variant="subtle"
                                            color="grape"
                                            onClick={() =>
                                              setSnapTarget({
                                                cell,
                                                teamName: row.teamName,
                                                challengeTitle: c.title,
                                              })
                                            }
                                          >
                                            <Icon path={mdiFileTree} size={0.7} />
                                          </ActionIcon>
                                        </Indicator>
                                      </Tooltip>
                                    )}
                                  </Group>
                                </Group>
                                {cell.containerIp && (
                                  <CopyButton value={`${cell.containerIp}:${cell.containerPort ?? ''}`}>
                                    {({ copied, copy }) => (
                                      <Tooltip
                                        label={
                                          copied
                                            ? t('game.tooltip.copy.copied', 'Copied')
                                            : t('game.tooltip.copy.ip_port', 'Copy IP:port')
                                        }
                                      >
                                        <Text
                                          className={misc.ffmono}
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
                                {cell.currentFlag && (
                                  <CopyButton value={cell.currentFlag}>
                                    {({ copied, copy }) => (
                                      <Tooltip
                                        label={
                                          copied
                                            ? t('game.tooltip.copy.copied', 'Copied')
                                            : t('admin.tooltip.ad_ops.copy_flag', 'Copy current flag')
                                        }
                                      >
                                        <Text
                                          className={misc.ffmono}
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
                  {visibleTeams.length === 0 && (
                    <Table.Tr>
                      <Table.Td colSpan={state.challenges.length + 1}>
                        <Text ta="center" c="dimmed" py="md" size="sm">
                          {t('admin.content.ad_ops.no_team_match', 'No teams match the filter.')}
                        </Text>
                      </Table.Td>
                    </Table.Tr>
                  )}
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
