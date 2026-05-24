import { Alert, Badge, Button, Group, Modal, Paper, Stack, Text } from '@mantine/core'
import { useDisclosure } from '@mantine/hooks'
import { mdiAlertCircleOutline, mdiOpenInNew, mdiSword, mdiVpn } from '@mdi/js'
import { Icon } from '@mdi/react'
import dayjs from 'dayjs'
import { FC, useMemo } from 'react'
import { useTranslation } from 'react-i18next'
import { useParams } from 'react-router'
import { ChallengePanel } from '@Components/ChallengePanel'
import { GameNoticePanel } from '@Components/GameNoticePanel'
import { TeamRank } from '@Components/TeamRank'
import { WithGameTab } from '@Components/WithGameTab'
import { WithNavBar } from '@Components/WithNavbar'
import { WithRole } from '@Components/WithRole'
import { useAdState } from '@Hooks/useGame'
import { useGameTeamInfo } from '@Hooks/useGame'
import { ChallengeType, Role } from '@Api'

const Challenges: FC = () => {
  const { id } = useParams()
  const numId = parseInt(id ?? '-1')
  const { t } = useTranslation()

  const { teamInfo } = useGameTeamInfo(numId)
  const hasAd = useMemo(() => {
    if (!teamInfo?.challenges) return false
    for (const list of Object.values(teamInfo.challenges)) {
      if (list?.some((c) => c.type === ChallengeType.AttackDefense)) return true
    }
    return false
  }, [teamInfo])

  const { adState } = useAdState(numId, hasAd)
  const [vpnOpened, vpnHandlers] = useDisclosure(false)

  const roundEndsIn = adState?.roundEndsAt
    ? Math.max(0, dayjs(adState.roundEndsAt).diff(dayjs(), 'second'))
    : null

  return (
    <WithNavBar width="90%">
      <WithRole requiredRole={Role.User}>
        <WithGameTab>
          <Group gap="sm" justify="space-between" align="flex-start" wrap="nowrap">
            <ChallengePanel />
            <Stack gap="sm" miw="22rem" maw="22rem">
              <Button
                component="a"
                href={`/games/${numId}/attack`}
                target="_blank"
                rel="noreferrer"
                variant="light"
                fullWidth
                leftSection={<Icon path={mdiSword} size={1} />}
                rightSection={<Icon path={mdiOpenInNew} size={0.8} />}
              >
                {t('game.button.attack')}
              </Button>
              {hasAd && (
                <>
                  <Paper p="sm" withBorder>
                    <Group justify="space-between" wrap="nowrap" align="center">
                      <Stack gap={0}>
                        <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
                          {t('game.content.ad.round', 'Round')}
                        </Text>
                        <Text fw="bold" size="lg">
                          {adState?.currentRound ?? '—'}
                        </Text>
                      </Stack>
                      <Stack gap={0} align="flex-end">
                        <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
                          {t('game.content.ad.round_ends', 'Round ends')}
                        </Text>
                        <Text fw="bold" size="lg">
                          {roundEndsIn === null
                            ? t('game.content.ad.no_round_yet', 'No round yet — warmup')
                            : `${roundEndsIn}s`}
                        </Text>
                      </Stack>
                    </Group>
                    {adState?.currentRound === 0 && (
                      <Badge color="blue" variant="light" size="sm" mt="xs" w="100%">
                        {t('game.content.ad.warmup_pill', 'Warmup — scoring not yet active')}
                      </Badge>
                    )}
                  </Paper>
                  <Button
                    variant="default"
                    fullWidth
                    leftSection={<Icon path={mdiVpn} size={1} />}
                    onClick={vpnHandlers.open}
                  >
                    {t('game.button.ad.download_vpn', 'Download VPN config')}
                  </Button>
                </>
              )}
              <TeamRank />
              <GameNoticePanel />
            </Stack>
          </Group>

          <Modal
            opened={vpnOpened}
            onClose={vpnHandlers.close}
            title={t('game.content.ad.vpn_modal.title', 'VPN provisioning')}
            centered
          >
            <Stack gap="sm">
              <Alert color="blue" icon={<Icon path={mdiAlertCircleOutline} size={1} />}>
                {t(
                  'game.content.ad.vpn_modal.body',
                  'Per-team WireGuard config download is coming in a follow-up phase. For now, contact your operator for network access — A&D containers expose their service ports directly during the dev preview.'
                )}
              </Alert>
              <Group justify="flex-end">
                <Button onClick={vpnHandlers.close}>
                  {t('common.modal.confirm', 'Confirm')}
                </Button>
              </Group>
            </Stack>
          </Modal>
        </WithGameTab>
      </WithRole>
    </WithNavBar>
  )
}

export default Challenges
