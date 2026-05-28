import { Badge, Button, Group, Paper, Stack, Text } from '@mantine/core'
import { useDisclosure } from '@mantine/hooks'
import { mdiOpenInNew, mdiSword, mdiToolboxOutline } from '@mdi/js'
import { Icon } from '@mdi/react'
import dayjs from 'dayjs'
import { FC, useMemo } from 'react'
import { useTranslation } from 'react-i18next'
import { useParams } from 'react-router'
import { AdGuideModal } from '@Components/AdGuideModal'
import { ChallengePanel } from '@Components/ChallengePanel'
import { GameNoticePanel } from '@Components/GameNoticePanel'
import { TeamRank } from '@Components/TeamRank'
import { WithGameTab } from '@Components/WithGameTab'
import { WithNavBar } from '@Components/WithNavbar'
import { WithRole } from '@Components/WithRole'
import { useAdState } from '@Hooks/useGame'
import { useGameTeamInfo } from '@Hooks/useGame'
import { useTicker } from '@Hooks/useTicker'
import { ChallengeType, Role } from '@Api'

const Challenges: FC = () => {
  const { id } = useParams()
  const numId = parseInt(id ?? '-1')
  const { t } = useTranslation()

  const { teamInfo } = useGameTeamInfo(numId)
  // KotH and A&D both ride the AD-engine plumbing — the toolkit / SSH-jump /
  // VPN config / adState polling are equally relevant for both. Without
  // including KotH here, a pure-KotH game would skip useAdState polling
  // and the player would never see live state.
  const hasAd = useMemo(() => {
    if (!teamInfo?.challenges) return false
    for (const list of Object.values(teamInfo.challenges)) {
      if (list?.some((c) =>
        c.type === ChallengeType.AttackDefense || c.type === ChallengeType.KingOfTheHill
      )) return true
    }
    return false
  }, [teamInfo])

  const { adState } = useAdState(numId, hasAd)
  const [guideOpened, guideHandlers] = useDisclosure(false)

  // useTicker fires every 1s so the countdown actually counts down between
  // SWR refreshes (which only happen every 10s). Without this the value is
  // frozen until the next adState refetch.
  const now = useTicker()
  const roundEndsIn = adState?.roundEndsAt
    ? Math.max(0, dayjs(adState.roundEndsAt).diff(now, 'second'))
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
                    leftSection={<Icon path={mdiToolboxOutline} size={1} />}
                    onClick={guideHandlers.open}
                  >
                    {t('game.button.ad.open_toolkit', 'A&D Toolkit')}
                  </Button>
                </>
              )}
              <TeamRank />
              <GameNoticePanel />
            </Stack>
          </Group>

          {hasAd && (
            <AdGuideModal
              gameId={numId}
              opened={guideOpened}
              onClose={guideHandlers.close}
            />
          )}
        </WithGameTab>
      </WithRole>
    </WithNavBar>
  )
}

export default Challenges
