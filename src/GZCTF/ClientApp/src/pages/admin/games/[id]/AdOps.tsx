import {
  Alert,
  Badge,
  Box,
  Button,
  Card,
  Center,
  Code,
  CopyButton,
  Divider,
  Group,
  Loader,
  Modal,
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
  mdiDownload,
  mdiFileTree,
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

const AdOps: FC = () => {
  const { id } = useParams()
  const numId = parseInt(id ?? '-1', 10)
  const { t } = useTranslation()
  const { adminAdState: state, error, mutate } = useAdminAdState(numId)
  const [busy, setBusy] = useState(false)
  const [snapTarget, setSnapTarget] = useState<SnapTarget | null>(null)
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

  const now = useTicker()
  const roundEndsIn =
    state.roundEndsAt ? Math.max(0, dayjs(state.roundEndsAt).diff(now, 'second')) : null

  return (
    <WithGameEditTab>
      <SnapshotModal gameId={numId} target={snapTarget} onClose={() => setSnapTarget(null)} />
      <Stack gap="md">
        {/* Top status bar */}
        <Paper p="md" withBorder>
          <Group justify="space-between" align="center" wrap="wrap" gap="md">
            <Group gap="xl" wrap="wrap">
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
                    {t('admin.content.ad_ops.tick_summary', {
                      tick: c.tickSeconds,
                      lifetime: c.flagLifetimeTicks,
                      defaultValue: 'tick {{tick}}s · lifetime {{lifetime}} ticks',
                    })}
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
            <ScrollArea h="55vh" type="auto">
              <Table verticalSpacing="xs" striped highlightOnHover withColumnBorders>
                <Table.Thead className={tableClasses.thead}>
                  <Table.Tr>
                    <Table.Th className={tableClasses.corner}>
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
                      <Table.Td className={tableClasses.left}>
                        <Text truncate fw="bold" size="sm" maw="12rem">
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
                                </Group>
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
                                <Group gap={4} wrap="nowrap">
                                  <Button
                                    size="compact-xs"
                                    variant="subtle"
                                    leftSection={<Icon path={mdiRestart} size={0.7} />}
                                    onClick={() => restartCell(cell)}
                                  >
                                    {t('admin.button.ad_ops.restart', 'Restart')}
                                  </Button>
                                  {cell.snapshotAvailable && (
                                    <Button
                                      size="compact-xs"
                                      variant="subtle"
                                      color="grape"
                                      leftSection={<Icon path={mdiFileTree} size={0.7} />}
                                      onClick={() =>
                                        setSnapTarget({
                                          cell,
                                          teamName: row.teamName,
                                          challengeTitle: c.title,
                                        })
                                      }
                                    >
                                      {cell.changedFileCount != null
                                        ? t('admin.button.ad_ops.snapshot.with_count', {
                                            count: cell.changedFileCount,
                                            defaultValue: 'Snapshot ({{count}})',
                                          })
                                        : t('admin.button.ad_ops.snapshot.label', 'Snapshot')}
                                    </Button>
                                  )}
                                </Group>
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
