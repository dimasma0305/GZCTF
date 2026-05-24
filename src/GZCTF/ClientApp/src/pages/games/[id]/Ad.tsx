import {
  Alert,
  Badge,
  Box,
  Button,
  Card,
  Center,
  CopyButton,
  Divider,
  Group,
  Loader,
  Paper,
  SimpleGrid,
  Stack,
  Text,
  TextInput,
  Title,
  Tooltip,
} from '@mantine/core'
import { showNotification } from '@mantine/notifications'
import {
  mdiAlertCircleOutline,
  mdiCheck,
  mdiCloseCircle,
  mdiContentCopy,
  mdiDownload,
  mdiExclamationThick,
  mdiRestart,
  mdiSwordCross,
} from '@mdi/js'
import { Icon } from '@mdi/react'
import dayjs from 'dayjs'
import { FC, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useParams } from 'react-router'
import { WithGameTab } from '@Components/WithGameTab'
import { showErrorMsg } from '@Utils/Shared'
import { useAdState, useGame } from '@Hooks/useGame'
import api, { AdSubmitResultModel } from '@Api'

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

const submitColor = (status: string) => {
  switch (status) {
    case 'accepted':
      return 'teal'
    case 'duplicate':
      return 'gray'
    case 'expired':
      return 'orange'
    case 'wrong':
    case 'self_attack':
      return 'red'
    case 'not_started':
      return 'blue'
    default:
      return 'gray'
  }
}

const Ad: FC = () => {
  const { id } = useParams()
  const numId = parseInt(id ?? '-1', 10)
  const { t } = useTranslation()
  const { game } = useGame(numId)
  const { adState: state, mutate } = useAdState(numId)

  const [flag, setFlag] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [results, setResults] = useState<AdSubmitResultModel[]>([])
  const [resettingId, setResettingId] = useState<number | null>(null)

  const gameEnded = game?.end ? dayjs() > dayjs(game.end) : false

  const submit = async () => {
    if (!flag.trim()) return
    setSubmitting(true)
    try {
      const { data } = await api.game.gameAdSubmit(numId, { flag: flag.trim() })
      setResults((prev) => [data, ...prev].slice(0, 8))
      if (data.status === 'accepted') {
        showNotification({
          color: 'teal',
          icon: <Icon path={mdiCheck} size={1} />,
          title: t('game.notification.ad.attack_accepted',
            { points: data.points?.toFixed(2), defaultValue: 'Attack accepted (+{{points}} pts)' }),
          message: '',
        })
        setFlag('')
      }
      mutate()
    } catch (e) {
      showErrorMsg(e, t)
    } finally {
      setSubmitting(false)
    }
  }

  const reset = async (adTeamServiceId: number) => {
    setResettingId(adTeamServiceId)
    try {
      await api.game.gameAdResetService(numId, adTeamServiceId)
      showNotification({
        color: 'teal',
        icon: <Icon path={mdiRestart} size={1} />,
        title: t('game.notification.ad.reset_queued.title', 'Reset queued'),
        message: t('game.notification.ad.reset_queued.message',
          'Container will rebuild in seconds.'),
      })
      setTimeout(() => mutate(), 3_000)
    } catch (e) {
      showErrorMsg(e, t)
    } finally {
      setResettingId(null)
    }
  }

  if (!state) {
    return (
      <WithGameTab>
        <Center h="40vh">
          <Loader />
        </Center>
      </WithGameTab>
    )
  }

  if (state.services.length === 0) {
    return (
      <WithGameTab>
        <Center h="40vh">
          <Stack align="center" gap="xs">
            <Icon path={mdiSwordCross} size={2.5} color="var(--mantine-color-dimmed)" />
            <Text fw="bold" c="dimmed">
              {t('game.content.ad.empty.title', 'No A&D challenges in this game')}
            </Text>
            <Text size="sm" c="dimmed">
              {t('game.content.ad.empty.description',
                "If you expected to see services here, the operator hasn't created any A&D challenges or your team's containers haven't been provisioned yet.")}
            </Text>
          </Stack>
        </Center>
      </WithGameTab>
    )
  }

  const roundEndsIn =
    state.roundEndsAt ? Math.max(0, dayjs(state.roundEndsAt).diff(dayjs(), 'second')) : null

  return (
    <WithGameTab>
      <Stack gap="md">
        {/* Top status bar */}
        <Paper p="md" withBorder>
          <Group justify="space-between">
            <Group gap="xl">
              <Stack gap={0}>
                <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
                  {t('game.content.ad.round', 'Round')}
                </Text>
                <Text fw="bold" size="xl">
                  {state.currentRound || '—'}
                </Text>
              </Stack>
              <Stack gap={0}>
                <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
                  {t('game.content.ad.round_ends', 'Round ends')}
                </Text>
                <Text fw="bold" size="md">
                  {roundEndsIn === null
                    ? t('game.content.ad.no_round_yet', 'No round yet — warmup')
                    : `${roundEndsIn}s`}
                </Text>
              </Stack>
            </Group>
            {state.currentRound === 0 && (
              <Badge color="blue" variant="light" size="lg">
                {t('game.content.ad.warmup_pill', 'Warmup — scoring not yet active')}
              </Badge>
            )}
          </Group>
        </Paper>

        {/* Defend */}
        <Paper p="md" withBorder>
          <Group gap="xs" mb="sm">
            <Icon path={mdiSwordCross} size={1} />
            <Title order={4}>{t('game.content.ad.defend.title', 'Defend')}</Title>
          </Group>
          <Text size="sm" c="dimmed" mb="md">
            {t('game.content.ad.defend.description',
              "Your team's services. Keep them healthy + patched. Click any IP or flag to copy.")}
          </Text>
          <SimpleGrid cols={{ base: 1, md: 2, lg: 3 }}>
            {state.services.map((s) => (
              <Card key={s.adTeamServiceId} withBorder p="sm">
                <Stack gap="xs">
                  <Group justify="space-between" wrap="nowrap" align="flex-start">
                    <Text truncate fw="bold">{s.challengeTitle}</Text>
                    <Badge color={statusColor(s.lastCheckStatus)} size="sm" variant={s.lastCheckStatus ? 'filled' : 'light'}>
                      {s.lastCheckStatus ?? t('game.content.ad.no_checks_yet', 'no checks yet')}
                    </Badge>
                  </Group>

                  {s.containerIp && (
                    <Group gap={4} align="center">
                      <Text size="xs" c="dimmed">
                        {t('game.content.ad.target', 'Target')}:
                      </Text>
                      <CopyButton value={`${s.containerIp}:${s.containerPort ?? ''}`}>
                        {({ copied, copy }) => (
                          <Tooltip label={copied ? 'Copied' : 'Copy IP:port'}>
                            <Text
                              ff="monospace"
                              size="sm"
                              style={{ cursor: 'pointer' }}
                              onClick={copy}
                            >
                              {s.containerIp}:{s.containerPort}
                            </Text>
                          </Tooltip>
                        )}
                      </CopyButton>
                    </Group>
                  )}

                  {s.currentFlag && (
                    <Group gap={4} align="flex-start">
                      <Text size="xs" c="dimmed">
                        {t('game.content.ad.flag_to_defend', 'Defending')}:
                      </Text>
                      <CopyButton value={s.currentFlag}>
                        {({ copied, copy }) => (
                          <Tooltip label={copied ? 'Copied' : 'Copy flag'}>
                            <Text
                              ff="monospace"
                              size="xs"
                              c="dimmed"
                              truncate
                              style={{ cursor: 'pointer' }}
                              onClick={copy}
                            >
                              {s.currentFlag}
                            </Text>
                          </Tooltip>
                        )}
                      </CopyButton>
                    </Group>
                  )}

                  <Group gap="xs" mt={4}>
                    <Tooltip
                      label={
                        !s.canReset && s.resetCooldownSecondsRemaining
                          ? t('game.tooltip.ad.reset_cooldown', {
                              seconds: s.resetCooldownSecondsRemaining,
                              defaultValue: 'On cooldown — {{seconds}}s remaining',
                            })
                          : t('game.tooltip.ad.reset', 'Rebuild this container to baseline (you lose SLA during the rebuild)')
                      }
                    >
                      <Button
                        size="compact-xs"
                        variant="default"
                        leftSection={<Icon path={mdiRestart} size={0.7} />}
                        loading={resettingId === s.adTeamServiceId}
                        disabled={!s.canReset}
                        onClick={() => reset(s.adTeamServiceId)}
                      >
                        {!s.canReset && s.resetCooldownSecondsRemaining
                          ? `${s.resetCooldownSecondsRemaining}s`
                          : t('game.button.ad.reset', 'Reset')}
                      </Button>
                    </Tooltip>
                    {gameEnded && (
                      <Button
                        size="compact-xs"
                        variant="default"
                        leftSection={<Icon path={mdiDownload} size={0.7} />}
                        component="a"
                        href={api.game.gameAdDownloadSnapshotUrl(numId, s.adTeamServiceId)}
                      >
                        {t('game.button.ad.snapshot', 'Snapshot')}
                      </Button>
                    )}
                  </Group>
                </Stack>
              </Card>
            ))}
          </SimpleGrid>
        </Paper>

        {/* Attack */}
        <Paper p="md" withBorder>
          <Group gap="xs" mb="sm">
            <Icon path={mdiSwordCross} size={1} />
            <Title order={4}>{t('game.content.ad.attack.title', 'Attack')}</Title>
          </Group>
          <Text size="sm" c="dimmed" mb="md">
            {t('game.content.ad.attack.description',
              'Submit a flag you captured from another team.')}
          </Text>
          <Group align="flex-end">
            <TextInput
              style={{ flex: 1 }}
              placeholder={t('game.content.ad.attack.placeholder', 'flag{...}')}
              value={flag}
              onChange={(e) => setFlag(e.currentTarget.value)}
              ff="monospace"
              size="md"
            />
            <Button
              size="md"
              loading={submitting}
              disabled={!flag.trim()}
              onClick={submit}
            >
              {t('game.button.ad.submit', 'Submit attack')}
            </Button>
          </Group>

          {results.length > 0 && (
            <>
              <Divider my="md" label={t('game.content.ad.attack.recent', 'Recent submissions')} labelPosition="left" />
              <Stack gap={4}>
                {results.map((r, i) => (
                  <Alert
                    key={i}
                    color={submitColor(r.status)}
                    icon={
                      <Icon
                        path={
                          r.status === 'accepted'
                            ? mdiCheck
                            : r.status === 'wrong' || r.status === 'self_attack'
                              ? mdiCloseCircle
                              : mdiAlertCircleOutline
                        }
                        size={1}
                      />
                    }
                  >
                    <Text size="sm">
                      <b>{t(`game.content.ad.submit_status.${r.status}`, r.status)}</b>
                      {r.points && ` — +${r.points.toFixed(2)} pts`}
                      {r.flagPlantedAtRound && ` · planted at round ${r.flagPlantedAtRound}`}
                      {r.message && <Text component="span" c="dimmed" size="xs"> — {r.message}</Text>}
                    </Text>
                  </Alert>
                ))}
              </Stack>
            </>
          )}
        </Paper>
      </Stack>
    </WithGameTab>
  )
}

export default Ad
