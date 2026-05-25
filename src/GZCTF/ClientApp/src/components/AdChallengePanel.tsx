import {
  Alert,
  Badge,
  Button,
  CopyButton,
  Group,
  Loader,
  Stack,
  Text,
  Tooltip,
} from '@mantine/core'
import { showNotification } from '@mantine/notifications'
import { mdiAlertCircleOutline, mdiConsole, mdiDownload, mdiRestart } from '@mdi/js'
import { Icon } from '@mdi/react'
import { FC, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { showErrorMsg } from '@Utils/Shared'
import { useAdState } from '@Hooks/useGame'
import api, { AdTeamServiceStateModel } from '@Api'
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

interface AdChallengePanelProps {
  gameId: number
  challengeId: number
}

/**
 * Per-challenge A&amp;D status block: container IP+port, the current flag the
 * team should defend, the latest health-check verdict, and a reset-to-baseline
 * button. The token-management UI and the API/curl docs live in the A&amp;D
 * Toolkit modal (sidebar button) so this panel only shows live per-team
 * operational state.
 */
export const AdChallengePanel: FC<AdChallengePanelProps> = ({ gameId, challengeId }) => {
  const { t } = useTranslation()
  const { adState, mutate: mutateState } = useAdState(gameId)
  const { data: sshKey } = api.game.useAdGameGetSshKey(gameId)
  const [resetting, setResetting] = useState(false)

  const service: AdTeamServiceStateModel | undefined = adState?.services.find(
    (s) => s.challengeId === challengeId
  )

  // Render the `ssh <id>@host -p <port>` snippet the player runs to shell
  // into their container for THIS challenge. Host/port come from the SSH
  // key info endpoint (operator-configured Ad:Ssh:PublicHost/Port). We
  // only show the snippet once they've registered a key — otherwise it
  // would just confuse a player whose first auth would fail anyway.
  const renderSshHint = () => {
    if (!sshKey?.jumpHost) return null
    const [host, port] = sshKey.jumpHost.split(':')
    const cmd = `ssh ${challengeId}@${host} -p ${port ?? '22022'}`
    return (
      <Group gap={6} align="center" wrap="nowrap">
        <Tooltip
          label={
            sshKey.exists
              ? t('game.tooltip.ad.ssh_ready', 'SSH key is registered — connect any time')
              : t('game.tooltip.ad.ssh_not_ready', 'Register an SSH key in the Toolkit first')
          }
        >
          <Group gap={4} wrap="nowrap" style={{ opacity: sshKey.exists ? 1 : 0.5 }}>
            <Icon path={mdiConsole} size={0.6} />
            <Text size="xs" c="dimmed">
              SSH:
            </Text>
          </Group>
        </Tooltip>
        <CopyButton value={cmd}>
          {({ copied, copy }) => (
            <Tooltip
              label={
                copied
                  ? t('game.tooltip.copy.copied', 'Copied')
                  : t('game.tooltip.copy.ssh_cmd', 'Copy ssh command')
              }
            >
              <Text
                className={misc.ffmono}
                size="xs"
                c={sshKey.exists ? undefined : 'dimmed'}
                truncate
                style={{ cursor: 'pointer' }}
                onClick={copy}
              >
                {cmd}
              </Text>
            </Tooltip>
          )}
        </CopyButton>
      </Group>
    )
  }

  const onReset = async () => {
    if (!service) return
    setResetting(true)
    try {
      await api.game.gameAdResetService(gameId, service.adTeamServiceId)
      showNotification({
        color: 'teal',
        icon: <Icon path={mdiRestart} size={1} />,
        title: t('game.notification.ad.reset_queued.title', 'Reset queued'),
        message: t('game.notification.ad.reset_queued.message', 'Container will rebuild in seconds.'),
      })
      setTimeout(() => mutateState(), 3_000)
    } catch (e) {
      showErrorMsg(e, t)
    } finally {
      setResetting(false)
    }
  }

  if (!adState) {
    return (
      <Group justify="center" py="md">
        <Loader size="sm" />
      </Group>
    )
  }

  if (!service) {
    return (
      <Alert
        icon={<Icon path={mdiAlertCircleOutline} size={1} />}
        color="orange"
        title={t('game.content.ad.no_service.title', 'No service for your team yet')}
      >
        {t(
          'game.content.ad.no_service.description',
          "If you expected a container here, it hasn't been provisioned yet. Ask the operator to run \"Ensure containers\" from the A&D Ops console."
        )}
      </Alert>
    )
  }

  return (
    <Stack gap={4}>
      <Group justify="space-between" wrap="nowrap" align="center">
        <Group gap="xs" wrap="nowrap">
          <Text fw="bold" size="sm">
            {t('game.content.ad.defend_target', 'Your service')}
          </Text>
          <Badge
            size="sm"
            color={statusColor(service.lastCheckStatus)}
            variant={service.lastCheckStatus ? 'filled' : 'light'}
          >
            {service.lastCheckStatus ?? t('game.content.ad.no_checks_yet', 'no checks yet')}
          </Badge>
        </Group>
        <Tooltip
          label={
            !service.canReset && service.resetCooldownSecondsRemaining
              ? t('game.tooltip.ad.reset_cooldown', {
                  seconds: service.resetCooldownSecondsRemaining,
                  defaultValue: 'On cooldown — {{seconds}}s remaining',
                })
              : t('game.tooltip.ad.reset', 'Rebuild this container to baseline (you lose SLA during the rebuild)')
          }
        >
          <Button
            size="compact-xs"
            variant="default"
            leftSection={<Icon path={mdiRestart} size={0.7} />}
            loading={resetting}
            disabled={!service.canReset}
            onClick={onReset}
          >
            {!service.canReset && service.resetCooldownSecondsRemaining
              ? `${service.resetCooldownSecondsRemaining}s`
              : t('game.button.ad.reset', 'Reset')}
          </Button>
        </Tooltip>
      </Group>

      {service.containerIp && (
        <Group gap={6} align="center" wrap="nowrap">
          <Text size="xs" c="dimmed">
            {t('game.content.ad.target', 'Target')}:
          </Text>
          <CopyButton value={`${service.containerIp}:${service.containerPort ?? ''}`}>
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
                  size="sm"
                  style={{ cursor: 'pointer' }}
                  onClick={copy}
                >
                  {service.containerIp}:{service.containerPort}
                </Text>
              </Tooltip>
            )}
          </CopyButton>
        </Group>
      )}

      {service.currentFlag && (
        <Group gap={6} align="flex-start" wrap="nowrap">
          <Text size="xs" c="dimmed">
            {t('game.content.ad.flag_to_defend', 'Defending')}:
          </Text>
          <CopyButton value={service.currentFlag}>
            {({ copied, copy }) => (
              <Tooltip
                label={
                  copied
                    ? t('game.tooltip.copy.copied', 'Copied')
                    : t('game.tooltip.copy.flag', 'Copy flag')
                }
              >
                <Text
                  className={misc.ffmono}
                  size="xs"
                  c="dimmed"
                  truncate
                  style={{ cursor: 'pointer' }}
                  onClick={copy}
                >
                  {service.currentFlag}
                </Text>
              </Tooltip>
            )}
          </CopyButton>
        </Group>
      )}

      {renderSshHint()}

      {service.snapshotAvailable && (
        <Group gap={6} align="center" wrap="nowrap">
          <Text size="xs" c="dimmed">
            {t('game.content.ad.snapshot', 'Post-game snapshot')}:
          </Text>
          <Tooltip
            label={t('game.tooltip.ad.snapshot',
              'Download your container as a loadable Docker image (docker load -i …)')}
          >
            <Button
              component="a"
              href={api.game.gameAdDownloadSnapshotUrl(gameId, service.adTeamServiceId)}
              download
              size="compact-xs"
              variant="light"
              leftSection={<Icon path={mdiDownload} size={0.7} />}
            >
              {t('game.button.ad.download_snapshot', 'Download .tar.gz')}
            </Button>
          </Tooltip>
        </Group>
      )}
    </Stack>
  )
}
