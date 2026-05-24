import {
  Alert,
  Badge,
  Box,
  Button,
  Code,
  CopyButton,
  Divider,
  Group,
  Loader,
  Modal,
  Stack,
  Text,
  Tooltip,
} from '@mantine/core'
import { showNotification } from '@mantine/notifications'
import { mdiAlertCircleOutline, mdiCheck, mdiContentCopy, mdiKeyChain, mdiRestart } from '@mdi/js'
import { Icon } from '@mdi/react'
import dayjs from 'dayjs'
import { FC, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { showErrorMsg } from '@Utils/Shared'
import { useAdState, useAdTokenHint } from '@Hooks/useGame'
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
 * Per-challenge A&D status + API-submit docs panel. Rendered inside the
 * challenge modal in place of the web flag-submit form. Pulls the team-scoped
 * service state (IP, current flag, last-check status, reset cooldown) and
 * shows a curl example wired to the team's API token.
 */
export const AdChallengePanel: FC<AdChallengePanelProps> = ({ gameId, challengeId }) => {
  const { t } = useTranslation()
  const { adState, mutate: mutateState } = useAdState(gameId)
  const { adTokenHint, mutate: mutateHint } = useAdTokenHint(gameId)

  const [resetting, setResetting] = useState(false)
  const [rotating, setRotating] = useState(false)
  const [freshToken, setFreshToken] = useState<string | null>(null)
  const [tokenModalOpen, setTokenModalOpen] = useState(false)

  const service: AdTeamServiceStateModel | undefined = adState?.services.find(
    (s) => s.challengeId === challengeId
  )

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

  const onRotate = async () => {
    setRotating(true)
    try {
      const { data } = await api.game.gameAdRotateToken(gameId)
      setFreshToken(data.token)
      setTokenModalOpen(true)
      mutateHint()
    } catch (e) {
      showErrorMsg(e, t)
    } finally {
      setRotating(false)
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

  const curlExample = [
    `curl -X POST ${window.location.origin}/api/Game/${gameId}/Ad/Submit \\`,
    `  -H "Authorization: Bearer ${adTokenHint?.exists ? adTokenHint.hint : '<your-token>'}" \\`,
    `  -H "Content-Type: application/json" \\`,
    `  -d '{"flags":["flag{captured_from_team_X}","flag{another_capture}"]}'`,
  ].join('\n')

  return (
    <>
      <Stack gap="sm">
        {/* Status: IP + flag + check + reset */}
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
        </Stack>

        <Divider />

        {/* API submit panel */}
        <Stack gap={4}>
          <Group justify="space-between" align="center" wrap="nowrap">
            <Group gap="xs">
              <Icon path={mdiKeyChain} size={0.8} />
              <Text fw="bold" size="sm">
                {t('game.content.ad.submit_via_api', 'Submit flags via API')}
              </Text>
            </Group>
            <Group gap="xs">
              {adTokenHint?.exists ? (
                <Text size="xs" c="dimmed" className={misc.ffmono}>
                  {adTokenHint.hint}
                </Text>
              ) : (
                <Text size="xs" c="dimmed">
                  {t('game.content.ad.no_token_yet', 'No token yet')}
                </Text>
              )}
              {adTokenHint?.canManage && (
                <Button
                  size="compact-xs"
                  variant="default"
                  loading={rotating}
                  onClick={onRotate}
                >
                  {adTokenHint.exists
                    ? t('game.button.ad.rotate_token', 'Rotate token')
                    : t('game.button.ad.generate_token', 'Generate token')}
                </Button>
              )}
            </Group>
          </Group>

          {!adTokenHint?.canManage && !adTokenHint?.exists && (
            <Text size="xs" c="dimmed">
              {t(
                'game.content.ad.token_captain_only',
                'Only your team captain can generate the API token. Ask them to open this panel and click Generate token.'
              )}
            </Text>
          )}

          <Code block className={misc.ffmono} style={{ fontSize: '0.75rem' }}>
            {curlExample}
          </Code>

          <Group justify="space-between">
            <Text size="xs" c="dimmed">
              {t(
                'game.content.ad.api_note',
                'Batch submission only — pass one or more captured flags in a single request (max 100). Per-flag results are returned in input order.'
              )}
            </Text>
            <CopyButton value={curlExample}>
              {({ copied, copy }) => (
                <Button
                  size="compact-xs"
                  variant="subtle"
                  leftSection={<Icon path={mdiContentCopy} size={0.7} />}
                  onClick={copy}
                >
                  {copied
                    ? t('game.tooltip.copy.copied', 'Copied')
                    : t('game.button.ad.copy_curl', 'Copy curl')}
                </Button>
              )}
            </CopyButton>
          </Group>

          {adTokenHint?.exists && (
            <Text size="xs" c="dimmed">
              {t('game.content.ad.last_used', 'Last used')}:{' '}
              {adTokenHint.lastUsedAt
                ? dayjs(adTokenHint.lastUsedAt).fromNow()
                : t('game.content.ad.never_used', 'never')}
            </Text>
          )}
        </Stack>
      </Stack>

      {/* Fresh-token reveal modal — shows plaintext exactly once */}
      <Modal
        opened={tokenModalOpen}
        onClose={() => {
          setTokenModalOpen(false)
          setFreshToken(null)
        }}
        title={t('game.content.ad.token_modal.title', 'Your new A&D API token')}
        centered
      >
        <Stack gap="sm">
          <Alert color="orange" icon={<Icon path={mdiAlertCircleOutline} size={1} />}>
            {t(
              'game.content.ad.token_modal.warning',
              'Save this token now — it will not be shown again. The previous token (if any) has been invalidated.'
            )}
          </Alert>
          <Box style={{ position: 'relative' }}>
            <Code block className={misc.ffmono}>
              {freshToken}
            </Code>
          </Box>
          <Group justify="flex-end">
            <CopyButton value={freshToken ?? ''}>
              {({ copied, copy }) => (
                <Button
                  variant="default"
                  leftSection={<Icon path={copied ? mdiCheck : mdiContentCopy} size={0.8} />}
                  onClick={copy}
                >
                  {copied
                    ? t('game.tooltip.copy.copied', 'Copied')
                    : t('game.button.ad.copy_token', 'Copy token')}
                </Button>
              )}
            </CopyButton>
            <Button onClick={() => setTokenModalOpen(false)}>
              {t('common.modal.confirm', 'Confirm')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  )
}
